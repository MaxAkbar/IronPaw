using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class CreateSkillTool : ITool
{
    private readonly IToolRegistrar _registrar;
    private readonly RoslynSkillCompiler _compiler;
    private readonly HashSet<string> _usedToolNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private int _compilationCounter;

    public CreateSkillTool(IToolRegistrar registrar)
    {
        _registrar = registrar;
        _compiler = new RoslynSkillCompiler();
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
        if (!call.Arguments.TryGetProperty("source_code", out var sourceElement) ||
            sourceElement.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult(ToolResult.Error("Missing required string argument: source_code"));
        }

        var sourceCode = sourceElement.GetString()!;
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
        var compilationResult = _compiler.Compile(sourceCode, assemblyName);

        if (!compilationResult.IsSuccess)
        {
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
