using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class WebExtractStructuredTool : IBrowserBoundTool
{
    public string? BrowserBinding => (_webBrowser as IBoundWebBrowserService)?.BrowserBinding;
    private readonly IWebBrowserService _webBrowser;

    public WebExtractStructuredTool(IWebBrowserService webBrowser)
    {
        _webBrowser = webBrowser;
        Descriptor = new ToolDescriptor(
            Name: "web_extract_structured",
            Description:
            "Extracts structured data from the current web page in a browser session. " +
            "Supports metadata, tables, and lists.",
            ParametersSchema: JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    session_id = new
                    {
                        type = "string",
                        description = "Optional browser session ID. Defaults to current run scope."
                    },
                    include_metadata = new
                    {
                        type = "boolean",
                        description = "Include page metadata (title/meta/canonical/lang). Default true."
                    },
                    include_tables = new
                    {
                        type = "boolean",
                        description = "Include extracted table data. Default true."
                    },
                    include_lists = new
                    {
                        type = "boolean",
                        description = "Include extracted ordered/unordered list data. Default true."
                    },
                    root_selector = new
                    {
                        type = "string",
                        description = "Optional CSS selector to scope extraction to a specific page region."
                    },
                    table_selector = new
                    {
                        type = "string",
                        description = "Optional CSS selector for table extraction candidates."
                    },
                    list_selector = new
                    {
                        type = "string",
                        description = "Optional CSS selector for list extraction candidates."
                    },
                    max_items = new
                    {
                        type = "integer",
                        description = "Maximum number of tables/lists to return per type (1-50). Default 10."
                    }
                },
                required = Array.Empty<string>()
            }),
            Group: "group:web");
    }

    public ToolDescriptor Descriptor { get; }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        if (context.ToolPolicy is { } policy ? ToolPolicyEvaluator.Apply([this], policy).Count != 1 : BrowserBinding is not null)
            return ToolResult.Error("The reviewed browser configuration is not granted by this workflow and all its ancestors.");
        if (!TryGetOptionalInt(call.Arguments, "max_items", out var maxItems, out var maxItemsError))
        {
            return ToolResult.Error(maxItemsError!);
        }

        if (!TryGetOptionalBool(call.Arguments, "include_metadata", out var includeMetadata, out var metadataError))
        {
            return ToolResult.Error(metadataError!);
        }

        if (!TryGetOptionalBool(call.Arguments, "include_tables", out var includeTables, out var tablesError))
        {
            return ToolResult.Error(tablesError!);
        }

        if (!TryGetOptionalBool(call.Arguments, "include_lists", out var includeLists, out var listsError))
        {
            return ToolResult.Error(listsError!);
        }

        var sessionId = WebToolSessionId.ResolveOrCreate(TryGetOptionalString(call.Arguments, "session_id"), context);
        (_webBrowser as IWebBrowserRunScope)?.BindSession(sessionId, context.ScopeRunId);
        var request = new WebStructuredDataRequest(
            SessionId: sessionId,
            IncludeMetadata: includeMetadata ?? true,
            IncludeTables: includeTables ?? true,
            IncludeLists: includeLists ?? true,
            MaxItems: Math.Clamp(maxItems ?? 10, 1, 50),
            RootSelector: TryGetOptionalString(call.Arguments, "root_selector"),
            TableSelector: TryGetOptionalString(call.Arguments, "table_selector"),
            ListSelector: TryGetOptionalString(call.Arguments, "list_selector"));

        WebStructuredDataResult result;
        try
        {
            result = await _webBrowser.ExtractStructuredDataAsync(request, ct);
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Error(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Error(ex.Message);
        }

        var payload = JsonSerializer.Serialize(new
        {
            session_id = result.SessionId,
            url = result.Url,
            title = result.Title,
            retrieved_at = result.RetrievedAt.ToString("O"),
            metadata = result.Metadata,
            tables = result.Tables.Select(table => new
            {
                index = table.Index,
                headers = table.Headers,
                rows = table.Rows
            }),
            lists = result.Lists.Select(list => new
            {
                index = list.Index,
                kind = list.Kind,
                items = list.Items
            })
        });

        return ToolResult.Success(
            payload,
            ContentTrust.Untrusted,
            details: new Dictionary<string, object?>
            {
                ["observability_event_type"] = "web.structured_extracted",
                ["session_id"] = result.SessionId,
                ["url"] = result.Url,
                ["table_count"] = result.Tables.Count,
                ["list_count"] = result.Lists.Count
            });
    }

    private static bool TryGetOptionalInt(
        JsonElement arguments,
        string propertyName,
        out int? value,
        out string? error)
    {
        value = null;
        error = null;
        if (!arguments.TryGetProperty(propertyName, out var node) || node.ValueKind is JsonValueKind.Null)
        {
            return true;
        }

        if (node.ValueKind is not JsonValueKind.Number || !node.TryGetInt32(out var parsed))
        {
            error = $"{propertyName} must be an integer.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryGetOptionalBool(
        JsonElement arguments,
        string propertyName,
        out bool? value,
        out string? error)
    {
        value = null;
        error = null;

        if (!arguments.TryGetProperty(propertyName, out var node) || node.ValueKind is JsonValueKind.Null)
        {
            return true;
        }

        if (node.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            error = $"{propertyName} must be a boolean.";
            return false;
        }

        value = node.GetBoolean();
        return true;
    }

    private static string? TryGetOptionalString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var node) || node.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (node.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        var value = node.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
