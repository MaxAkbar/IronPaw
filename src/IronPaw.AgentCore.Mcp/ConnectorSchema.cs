using System.Text;
using System.Text.Json;
using Json.Schema;

namespace IronPaw.AgentCore.Mcp;

internal sealed class ConnectorSchema
{
    private readonly JsonSchema _schema;
    public JsonElement Document { get; }

    public ConnectorSchema(JsonElement document, bool input)
    {
        if (document.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(document.GetRawText()) > 65_536)
            throw new ArgumentException("A connector schema must be an object of at most 65536 UTF-8 bytes.");
        if (input && (!document.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "object"))
            throw new ArgumentException("A connector input schema must declare type object.");
        Document = document.Clone();
        var remaining = 1024;
        CheckSchema(Document, 0, ref remaining);
        _schema = JsonSchema.Build(Document, new BuildOptions {
            Dialect = Dialect.Draft202012.With([], allowUnknownKeywords: false),
            SchemaRegistry = new SchemaRegistry { Fetch = static (_, _) => throw new InvalidOperationException("External schemas are disabled.") }
        });
    }

    public bool Accepts(JsonElement value) => _schema.Evaluate(value, new EvaluationOptions {
        OutputFormat = OutputFormat.Flag, RequireFormatValidation = true
    }).IsValid;

    private static void CheckSchema(JsonElement schema, int depth, ref int remaining)
    {
        if (depth > 16 || --remaining < 0) throw new ArgumentException("Connector schema complexity exceeds its limit.");
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False) return;
        if (schema.ValueKind != JsonValueKind.Object) throw new ArgumentException("Invalid connector subschema.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in schema.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new ArgumentException("Duplicate connector schema keyword.");
            // First slice deliberately accepts self-contained, non-regex Draft 2020-12 schemas.
            // No global registry, reference resolver, regex engine or custom vocabulary can add authority.
            if (property.Name is "$ref" or "$dynamicRef" or "$recursiveRef" or "$id" or "$vocabulary" or "pattern" or "patternProperties")
                throw new ArgumentException($"Connector schema keyword '{property.Name}' is not supported.");
            if (property.Name == "$schema" && property.Value.GetString() != "https://json-schema.org/draft/2020-12/schema")
                throw new ArgumentException("Connector schemas must use Draft 2020-12.");
            if (property.Name is "properties" or "$defs" or "dependentSchemas")
            {
                var children = new HashSet<string>(StringComparer.Ordinal);
                foreach (var child in property.Value.EnumerateObject())
                {
                    if (!children.Add(child.Name)) throw new ArgumentException("Duplicate connector schema property.");
                    CheckSchema(child.Value, depth + 1, ref remaining);
                }
            }
            else if (property.Name is "allOf" or "anyOf" or "oneOf" or "prefixItems")
                foreach (var child in property.Value.EnumerateArray()) CheckSchema(child, depth + 1, ref remaining);
            else if (property.Name is "items" or "additionalProperties" or "contains" or "not" or "if" or "then" or "else" or
                "propertyNames" or "unevaluatedProperties" or "unevaluatedItems")
                CheckSchema(property.Value, depth + 1, ref remaining);
        }
    }

    public static bool ValidValue(JsonElement value, int maxBytes)
    {
        if (value.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(value.GetRawText()) > maxBytes) return false;
        var nodes = 4096;
        return CheckValue(value, 0, ref nodes);
    }

    private static bool CheckValue(JsonElement value, int depth, ref int nodes)
    {
        if (depth > 32 || --nodes < 0) return false;
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || !CheckValue(property.Value, depth + 1, ref nodes)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (!CheckValue(item, depth + 1, ref nodes)) return false;
        return true;
    }

    public static string Canonical(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                Write(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) Write(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
