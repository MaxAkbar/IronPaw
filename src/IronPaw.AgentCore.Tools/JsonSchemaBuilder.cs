using System.Text.Json;

namespace IronPaw.AgentCore.Tools;

internal static class JsonSchemaBuilder
{
    public static JsonElement BuildObjectSchema(IEnumerable<SchemaProperty> properties)
    {
        var props = properties.ToArray();
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        var required = new List<string>();

        foreach (var property in props)
        {
            map[property.Name] = new Dictionary<string, object?>
            {
                ["type"] = property.Type,
                ["description"] = property.Description
            };

            if (property.Required)
            {
                required.Add(property.Name);
            }
        }

        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = map,
            ["required"] = required
        };

        return JsonSerializer.SerializeToElement(schema);
    }

    internal readonly record struct SchemaProperty(
        string Name,
        string Type,
        string Description,
        bool Required);
}
