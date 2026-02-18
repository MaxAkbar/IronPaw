using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class ToolSchemaNormalizer
{
    public LlmToolDefinition ToDefinition(ITool tool, string provider)
    {
        var normalized = Normalize(tool.Descriptor.ParametersSchema, provider);
        return new LlmToolDefinition(tool.Descriptor.Name, tool.Descriptor.Description, normalized);
    }

    private static JsonElement Normalize(JsonElement schema, string provider)
    {
        var root = schema;

        // Providers differ in schema strictness. Keep this central so tools remain provider-agnostic.
        if (provider.Equals("openai", StringComparison.OrdinalIgnoreCase))
        {
            if (!root.TryGetProperty("type", out _))
            {
                var wrapper = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = JsonSerializer.Deserialize<object>(root.GetRawText())
                };

                return JsonSerializer.SerializeToElement(wrapper);
            }

            return root.Clone();
        }

        if (provider.Equals("gemini", StringComparison.OrdinalIgnoreCase))
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, object?>>(root.GetRawText()) ?? [];
            map.Remove("$schema");
            return JsonSerializer.SerializeToElement(map);
        }

        return root.Clone();
    }
}
