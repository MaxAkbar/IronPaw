using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tests;

public sealed class TaskVerificationValidatorTests
{
    private static readonly CallerIdentity Owner = new("owner", "Owner", true);
    private static readonly WorkflowTaskGraph Graph = new("graph", "Build", [new("task", "Build")
        { AcceptanceCriteria = [new("criterion", "Build passes"), new("artifact", "File remains intact") { ArtifactIds = ["file"] }],
            ExpectedArtifacts = [new("file", "Output", TaskArtifactKind.File, "file.txt")] }]);

    [Fact]
    public void DefaultsGrantNoProcessAuthority()
    {
        var binding = Binding() with { VerificationAuthority = TaskVerificationAuthority.None, VerificationProfiles = [], CommandVerificationRules = [] };
        Assert.Equal(TaskVerificationAuthority.None, AgentTaskBindingValidator.Validate(Graph, Owner, binding).VerificationAuthority);
    }

    [Fact]
    public void ProfilesAreFrozenAndNormalizedWithoutFilesystemAccess()
    {
        var source = new[] { "build", "--no-restore" };
        var profile = Profile() with { Arguments = source.ToImmutableArray(), WorkingDirectory = "src\\project" };
        var normalized = AgentTaskBindingValidator.Validate(Graph, Owner, Binding() with { VerificationProfiles = [profile] });
        source[0] = "changed";
        Assert.Equal("build", normalized.VerificationProfiles[0].Arguments[0]);
        Assert.Equal("src/project", normalized.VerificationProfiles[0].WorkingDirectory);
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("caller")]
    [InlineData("legacy")]
    [InlineData("missing-profiles")]
    [InlineData("missing-rules")]
    [InlineData("unused")]
    [InlineData("unknown-task")]
    [InlineData("unknown-criterion")]
    [InlineData("unknown-profile")]
    [InlineData("duplicate-profile")]
    [InlineData("duplicate-mapping")]
    [InlineData("artifact-collision")]
    public void UnsupportedOrAmbiguousAuthorityAndMappingsAreRejected(string defect)
    {
        var binding = Binding();
        var caller = Owner;
        switch (defect)
        {
            case "authority": binding = binding with { VerificationAuthority = TaskVerificationAuthority.None }; break;
            case "caller": caller = Owner with { IsOwner = false }; break;
            case "legacy": binding = binding with { DependencyMode = TaskDependencyMode.BaseCommitOnly }; break;
            case "missing-profiles": binding = binding with { VerificationProfiles = [] }; break;
            case "missing-rules": binding = binding with { CommandVerificationRules = [] }; break;
            case "unused": binding = binding with { VerificationProfiles = [Profile(), Profile() with { ProfileId = "unused" }] }; break;
            case "unknown-task": binding = binding with { CommandVerificationRules = [new("other", "criterion", "build")] }; break;
            case "unknown-criterion": binding = binding with { CommandVerificationRules = [new("task", "other", "build")] }; break;
            case "unknown-profile": binding = binding with { CommandVerificationRules = [new("task", "criterion", "other")] }; break;
            case "duplicate-profile": binding = binding with { VerificationProfiles = [Profile(), Profile()] }; break;
            case "duplicate-mapping": binding = binding with { CommandVerificationRules = [new("task", "criterion", "build"), new("task", "criterion", "build")] }; break;
            case "artifact-collision": binding = binding with { ArtifactVerificationRules = [new("task", "artifact")], CommandVerificationRules = [new("task", "artifact", "build")] }; break;
        }
        var error = Record.Exception(() => AgentTaskBindingValidator.Validate(Graph, caller, binding));
        Assert.True(error is ArgumentException or NotSupportedException);
    }

    [Theory]
    [InlineData("relative-executable")]
    [InlineData("bad-digest")]
    [InlineData("nul-argument")]
    [InlineData("timeout")]
    [InlineData("too-long")]
    [InlineData("output")]
    [InlineData("cwd")]
    [InlineData("metadata")]
    [InlineData("test-without-report")]
    [InlineData("build-with-report")]
    public void InvalidProfilesAreRejected(string defect)
    {
        var profile = Profile();
        profile = defect switch
        {
            "relative-executable" => profile with { ExecutablePath = "worker.exe" },
            "bad-digest" => profile with { ExecutableDigest = "sha256:" + new string('A', 64) },
            "nul-argument" => profile with { Arguments = ["one\0two"] },
            "timeout" => profile with { Timeout = TimeSpan.Zero },
            "too-long" => profile with { Timeout = TimeSpan.FromDays(2) },
            "output" => profile with { MaxOutputBytes = 1_048_577 },
            "cwd" => profile with { WorkingDirectory = "../outside" },
            "metadata" => profile with { WorkingDirectory = ".git/hooks" },
            "test-without-report" => profile with { Kind = TaskVerificationKind.Tests },
            "build-with-report" => profile with { TestResultsPath = "report.trx" },
            _ => profile
        };
        Assert.Throws<ArgumentException>(() => AgentTaskBindingValidator.Validate(Graph, Owner, Binding() with { VerificationProfiles = [profile] }));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(3, 0, 0, 3)]
    [InlineData(2, 1, 1, 0)]
    [InlineData(3, 1, 0, 0)]
    public void ExitZeroCannotManufacturePassingTestEvidence(int total, int passed, int failed, int skipped)
    {
        var profile = Profile() with { Kind = TaskVerificationKind.Tests, TestResultsPath = "results/report.trx" };
        Assert.Throws<ArgumentException>(() => TaskVerificationValidator.ValidateCompletion(profile,
            Completion() with { Tests = new(total, passed, failed, skipped) }));
    }

    [Fact]
    public void ExecutedPassingTestSummaryIsRequiredAndBuildCannotClaimTests()
    {
        var completion = Completion() with { Tests = new(3, 2, 0, 1) };
        TaskVerificationValidator.ValidateCompletion(Profile() with { Kind = TaskVerificationKind.Tests, TestResultsPath = "results/report.trx" }, completion);
        Assert.Throws<ArgumentException>(() => TaskVerificationValidator.ValidateCompletion(Profile(), completion));
        Assert.Throws<ArgumentException>(() => TaskVerificationValidator.ValidateCompletion(Profile(), Completion() with { ExitCode = 1 }));
    }

    [Fact]
    public void OptionalCriteriaCannotAdmitProcessEffects()
    {
        var graph = Graph with { Tasks = [Graph.Tasks[0] with { AcceptanceCriteria = [new("criterion", "Optional check", Required: false)] }] };
        Assert.Throws<ArgumentException>(() => AgentTaskBindingValidator.Validate(graph, Owner, Binding()));
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("length")]
    [InlineData("limit")]
    [InlineData("truncated")]
    public void MalformedOutputEvidenceIsRejected(string defect)
    {
        var log = new TaskVerificationLog("hello", "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("hello"))), 5, false);
        log = defect switch
        {
            "digest" => log with { Digest = TaskVerificationIdentity.EmptyLogDigest },
            "length" => log with { ByteCount = 4 },
            "limit" => log with { Text = new string('a', 65_537), ByteCount = 65_537 },
            "truncated" => log with { Truncated = true },
            _ => log
        };
        Assert.Throws<ArgumentException>(() => TaskVerificationValidator.ValidateCompletion(Profile(), Completion() with { StandardOutput = log }));
    }

    private static TaskGraphAgentBinding Binding() => new(AgentTaskIdentity.RootRunId("graph").Value,
        Path.Combine(Path.GetTempPath(), "source"), new string('a', 40), Path.Combine(Path.GetTempPath(), "managed"), "test", "model",
        new(ToolProfile.Minimal), new(10, 10))
    {
        DependencyMode = TaskDependencyMode.AcceptedChanges, VerificationAuthority = TaskVerificationAuthority.TrustedLocalProcess,
        VerificationProfiles = [Profile()], CommandVerificationRules = [new("task", "criterion", "build")]
    };
    private static TaskVerificationProfile Profile() => new("build", Path.Combine(Path.GetTempPath(), "worker.exe"),
        "sha256:" + new string('a', 64), ["build"], TimeSpan.FromMinutes(1));
    private static TaskVerificationCompletion Completion() => new(TaskCommandOutcome.Passed, "worker", "Build passed", 0,
        new("", TaskVerificationIdentity.EmptyLogDigest, 0, false), new("", TaskVerificationIdentity.EmptyLogDigest, 0, false));
}
