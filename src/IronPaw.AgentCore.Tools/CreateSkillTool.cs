using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class CreateSkillTool : ITool
{
    private readonly IToolRegistrar _registrar;
    private readonly RoslynSkillCompiler _compiler;
    private readonly string? _persistDirectory;
    private readonly HashSet<string> _usedToolNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private int _compilationCounter;

    public CreateSkillTool(IToolRegistrar registrar, string? persistDirectory = null)
    {
        _registrar = registrar;
        _compiler = new RoslynSkillCompiler();
        _persistDirectory = string.IsNullOrWhiteSpace(persistDirectory) ? null : persistDirectory;
        Descriptor = new ToolDescriptor(
            Name: "create_skill",
            Description:
                "Compiles C# source code implementing ISkillAddin into a runtime skill. " +
                "The source must contain a class implementing IronPaw.AgentCore.Abstractions.ISkillAddin " +
                "with a parameterless constructor. On success the new skill tools become available " +
                "immediately in subsequent rounds.",
            ParametersSchema: JsonSchemaBuilder.BuildObjectSchema(
            [
                new("source_code", "string",
                    "Complete C# source code containing an ISkillAddin implementation. " +
                    "Must include all necessary using statements.",
                    Required: true)
            ]),
            Group: "group:runtime");
    }

    public ToolDescriptor Descriptor { get; }

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        if (!TryGetSourceCode(call.Arguments, out var sourceCode))
        {
            return Task.FromResult(ToolResult.Error("Missing required string argument: source_code"));
        }

        if (string.IsNullOrWhiteSpace(sourceCode))
        {
            return Task.FromResult(ToolResult.Error("source_code cannot be empty."));
        }

        int compilationId;
        lock (_lock)
        {
            compilationId = ++_compilationCounter;
        }

        var assemblyName = $"IronPaw.DynamicSkill_{compilationId}_{Guid.NewGuid():N}";
        var persistedAssemblyPath = string.Empty;

        RoslynCompilationResult compilationResult;
        if (_persistDirectory is not null)
        {
            persistedAssemblyPath = Path.Combine(_persistDirectory, $"{assemblyName}.dll");
            compilationResult = _compiler.CompileToFile(sourceCode, persistedAssemblyPath);
        }
        else
        {
            compilationResult = _compiler.Compile(sourceCode, assemblyName);
        }

        if (!compilationResult.IsSuccess)
        {
            if (!string.IsNullOrWhiteSpace(persistedAssemblyPath) && File.Exists(persistedAssemblyPath))
            {
                File.Delete(persistedAssemblyPath);
            }

            var errorPayload = JsonSerializer.Serialize(new
            {
                success = false,
                errors = compilationResult.Errors
            });
            return Task.FromResult(ToolResult.Error(errorPayload));
        }

        var addinTypes = compilationResult.Assembly!
            .GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface &&
                        typeof(ISkillAddin).IsAssignableFrom(t))
            .ToArray();

        if (addinTypes.Length == 0)
        {
            return Task.FromResult(ToolResult.Error(
                "Compilation succeeded but no ISkillAddin implementation was found in the source code."));
        }

        var registeredToolNames = new List<string>();
        var diagnostics = new List<string>();
        var newTools = new List<ITool>();

        if (!string.IsNullOrWhiteSpace(persistedAssemblyPath))
        {
            diagnostics.Add($"Saved skill add-in to '{persistedAssemblyPath}'.");
        }

        foreach (var addinType in addinTypes)
        {
            ISkillAddin addin;
            try
            {
                addin = (ISkillAddin)Activator.CreateInstance(addinType)!;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"Failed to instantiate '{addinType.FullName}': {ex.Message}");
                continue;
            }

            IReadOnlyList<ISkill> skills;
            try
            {
                skills = addin.CreateSkills() ?? [];
            }
            catch (Exception ex)
            {
                diagnostics.Add($"Addin '{addin.AddinId}' failed to create skills: {ex.Message}");
                continue;
            }

            foreach (var skill in skills)
            {
                string toolName;
                lock (_lock)
                {
                    var baseName = SkillToolAdapter.BuildToolName(skill.Descriptor.Name);
                    toolName = EnsureUniqueToolName(baseName);
                }

                var tool = new SkillToolAdapter(skill, addin.AddinId, toolName);
                newTools.Add(tool);
                registeredToolNames.Add(toolName);
            }
        }

        if (newTools.Count > 0)
        {
            _registrar.RegisterTools(newTools);
        }

        var successPayload = JsonSerializer.Serialize(new
        {
            success = true,
            registered_tools = registeredToolNames,
            diagnostics
        });

        return Task.FromResult(ToolResult.Success(successPayload));
    }

    private static bool TryGetSourceCode(JsonElement arguments, out string? sourceCode)
    {
        sourceCode = null;
        if (arguments.TryGetProperty("source_code", out var sourceElement) &&
            sourceElement.ValueKind == JsonValueKind.String)
        {
            sourceCode = sourceElement.GetString();
            return true;
        }

        if (arguments.TryGetProperty("raw", out var rawElement) &&
            rawElement.ValueKind == JsonValueKind.String)
        {
            var raw = rawElement.GetString();
            if (!string.IsNullOrWhiteSpace(raw) &&
                TryExtractSourceCode(raw, out var extracted))
            {
                sourceCode = extracted;
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractSourceCode(string raw, out string? sourceCode)
    {
        sourceCode = null;
        if (!raw.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("source_code", out var sourceElement) &&
                sourceElement.ValueKind == JsonValueKind.String)
            {
                sourceCode = sourceElement.GetString();
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private string EnsureUniqueToolName(string baseName)
    {
        if (_usedToolNames.Add(baseName))
        {
            return baseName;
        }

        var index = 2;
        while (true)
        {
            var candidate = $"{baseName}_{index}";
            if (_usedToolNames.Add(candidate))
            {
                return candidate;
            }

            index++;
        }
    }
}
