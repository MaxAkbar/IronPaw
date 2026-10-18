using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

/// <summary>Pure validation of frozen host verification authority and durable worker receipts.</summary>
public static class TaskVerificationValidator
{
    public static TaskGraphAgentBinding Validate(WorkflowTaskGraph graph, CallerIdentity caller, TaskGraphAgentBinding binding)
    {
        if (!Enum.IsDefined(binding.VerificationAuthority) || binding.VerificationProfiles.IsDefault || binding.CommandVerificationRules.IsDefault)
            throw new ArgumentException("Verification authority, profiles and mappings must be initialized and supported.", nameof(binding));
        var enabled = binding.VerificationAuthority != TaskVerificationAuthority.None || !binding.VerificationProfiles.IsEmpty || !binding.CommandVerificationRules.IsEmpty;
        if (!enabled) return binding;
        if (binding.VerificationAuthority != TaskVerificationAuthority.TrustedLocalProcess || !caller.IsOwner || binding.DependencyMode != TaskDependencyMode.AcceptedChanges)
            throw new NotSupportedException("Process verification requires explicit trusted local process authority, the owner caller and accepted predecessor changes.");
        if (binding.VerificationProfiles.IsEmpty || binding.CommandVerificationRules.IsEmpty)
            throw new ArgumentException("Trusted process verification requires profiles and criterion mappings.", nameof(binding));
        var profiles = ImmutableArray.CreateBuilder<TaskVerificationProfile>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in binding.VerificationProfiles)
        {
            ArgumentNullException.ThrowIfNull(profile);
            RequireIdentity(profile.ProfileId, "profile identity");
            if (!names.Add(profile.ProfileId)) throw new ArgumentException("Verification profile identities must be unique.", nameof(binding));
            if (string.IsNullOrWhiteSpace(profile.ExecutablePath) || profile.ExecutablePath != profile.ExecutablePath.Trim() ||
                profile.ExecutablePath.Any(char.IsControl) || !Path.IsPathFullyQualified(profile.ExecutablePath) ||
                profile.ExecutablePath.StartsWith("\\\\", StringComparison.Ordinal) || profile.ExecutablePath.AsSpan(Math.Min(2, profile.ExecutablePath.Length)).Contains(':') || !Digest(profile.ExecutableDigest))
                throw new ArgumentException("A verification executable requires an absolute local path and lowercase SHA256 digest.", nameof(binding));
            if (profile.Arguments.IsDefault || profile.Arguments.Any(argument => argument is null || argument.Contains('\0')) ||
                profile.Timeout <= TimeSpan.Zero || profile.Timeout > TimeSpan.FromDays(1) ||
                profile.MaxOutputBytes is < 1 or > 1_048_576 || !Enum.IsDefined(profile.Kind))
                throw new ArgumentException("Verification arguments, timeout, output limit or kind are invalid.", nameof(binding));
            var cwd = RelativePath(profile.WorkingDirectory, allowRoot: true);
            var results = profile.Kind == TaskVerificationKind.Tests
                ? RelativePath(profile.TestResultsPath, allowRoot: false)
                : profile.TestResultsPath is null ? null : throw new ArgumentException("Build profiles cannot declare test results.", nameof(binding));
            profiles.Add(profile with { ExecutablePath = Path.GetFullPath(profile.ExecutablePath), Arguments = profile.Arguments.ToImmutableArray(),
                WorkingDirectory = cwd, TestResultsPath = results });
        }
        var mappings = binding.ArtifactVerificationRules.Select(rule => (rule.TaskId, rule.CriterionId)).ToHashSet();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in binding.CommandVerificationRules)
        {
            if (rule is null || !mappings.Add((rule.TaskId, rule.CriterionId)) || !names.Contains(rule.ProfileId) ||
                !graph.Tasks.Any(task => task.TaskId == rule.TaskId && task.AcceptanceCriteria.Any(criterion => criterion.CriterionId == rule.CriterionId && criterion.Required)))
                throw new ArgumentException("Command verification mappings require a declared task, criterion and profile, with one verifier per criterion.", nameof(binding));
            used.Add(rule.ProfileId);
        }
        if (used.Count != names.Count) throw new ArgumentException("Every verification profile must be mapped to a criterion.", nameof(binding));
        return binding with { VerificationProfiles = profiles.ToImmutable(), CommandVerificationRules = binding.CommandVerificationRules.ToImmutableArray() };
    }

    public static void ValidateCompletion(TaskVerificationProfile profile, TaskVerificationCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (!Enum.IsDefined(completion.Outcome)) throw new ArgumentException("Unsupported verification outcome.", nameof(completion));
        RequireIdentity(completion.WorkerId, "worker identity");
        if (string.IsNullOrWhiteSpace(completion.Explanation)) throw new ArgumentException("A worker receipt requires an explanation.", nameof(completion));
        ValidateLog(profile, completion.StandardOutput);
        ValidateLog(profile, completion.StandardError);
        if (completion.Tests is { } tests && (tests.Total < 0 || tests.Passed < 0 || tests.Failed < 0 || tests.Skipped < 0 ||
            (long)tests.Total != (long)tests.Passed + tests.Failed + tests.Skipped))
            throw new ArgumentException("Test totals must be nonnegative and consistent.", nameof(completion));
        if (profile.Kind == TaskVerificationKind.Build && completion.Tests is not null)
            throw new ArgumentException("A build receipt cannot claim test execution.", nameof(completion));
        if (completion.Outcome == TaskCommandOutcome.Passed && (completion.ExitCode != 0 ||
            profile.Kind == TaskVerificationKind.Tests && (completion.Tests is not { Total: > 0, Passed: > 0, Failed: 0 })))
            throw new ArgumentException("Passing verification requires exit zero and, for tests, an executed passing test report.", nameof(completion));
    }

    private static void ValidateLog(TaskVerificationProfile profile, TaskVerificationLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (log.Text is null || !Digest(log.Digest)) throw new ArgumentException("A verification log requires text and a lowercase SHA256 digest.");
        var bytes = Encoding.UTF8.GetBytes(log.Text);
        if (bytes.Length > profile.MaxOutputBytes || log.ByteCount < bytes.Length ||
            log.Truncated && log.ByteCount <= bytes.Length || !log.Truncated && (log.ByteCount != bytes.Length ||
                log.Digest != "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes))))
            throw new ArgumentException("Verification log length, digest or truncation metadata is invalid.");
    }

    private static bool Digest(string? value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;
    private static void RequireIdentity(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException("A " + description + " must be nonblank, unpadded and contain no control characters.");
    }
    private static string RelativePath(string? value, bool allowRoot)
    {
        if (value == "." && allowRoot) return value;
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl) || Path.IsPathRooted(value) || value[0] is '/' or '\\' || value.Contains(':'))
            throw new ArgumentException("Verification paths must be safe relative paths.");
        var parts = value.Replace('\\', '/').Split('/');
        foreach (var part in parts)
        {
            var stem = part.Split('.')[0];
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(['*', '?', '<', '>', '|', '"']) >= 0 || part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '0' and <= '9')
                throw new ArgumentException("Verification paths cannot contain traversal, repository metadata, or nonportable segments.");
        }
        return string.Join('/', parts);
    }
}
