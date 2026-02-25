using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public sealed class WebNavigateTool : ITool
{
    private readonly IWebBrowserService _webBrowser;

    public WebNavigateTool(IWebBrowserService webBrowser)
    {
        _webBrowser = webBrowser;
        Descriptor = new ToolDescriptor(
            Name: "web_navigate",
            Description:
            "Navigates web pages. Supports actions: open, follow_link, click, submit_form. " +
            "Use session_id to maintain browser state across calls.",
            ParametersSchema: JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    action = new
                    {
                        type = "string",
                        description = "Navigation action: open | follow_link | click | submit_form."
                    },
                    session_id = new
                    {
                        type = "string",
                        description = "Optional browser session ID. Reuse it to keep page state."
                    },
                    url = new
                    {
                        type = "string",
                        description = "Target URL for action=open. Absolute URL is recommended."
                    },
                    link_index = new
                    {
                        type = "integer",
                        description = "Zero-based link index for follow_link/click."
                    },
                    link_text = new
                    {
                        type = "string",
                        description = "Optional text matcher for follow_link/click."
                    },
                    link_selector = new
                    {
                        type = "string",
                        description = "Optional CSS selector to locate link candidates for follow_link/click."
                    },
                    form_index = new
                    {
                        type = "integer",
                        description = "Zero-based form index for submit_form. Defaults to 0."
                    },
                    form_selector = new
                    {
                        type = "string",
                        description = "Optional CSS selector to locate form candidates for submit_form."
                    },
                    fields = new
                    {
                        type = "object",
                        description = "Optional form field overrides for submit_form.",
                        additionalProperties = new
                        {
                            type = "string"
                        }
                    }
                },
                required = new[] { "action" }
            }),
            Group: "group:web");
    }

    public ToolDescriptor Descriptor { get; }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
    {
        if (!TryGetRequiredString(call.Arguments, "action", out var actionRaw))
        {
            return ToolResult.Error("Missing required string parameter: action");
        }

        if (!TryParseAction(actionRaw, out var action))
        {
            return ToolResult.Error("Invalid action. Use one of: open, follow_link, click, submit_form.");
        }

        if (!TryGetOptionalInt(call.Arguments, "link_index", out var linkIndex, out var linkIndexError))
        {
            return ToolResult.Error(linkIndexError!);
        }

        if (!TryGetOptionalInt(call.Arguments, "form_index", out var formIndex, out var formIndexError))
        {
            return ToolResult.Error(formIndexError!);
        }

        if (!TryGetOptionalStringDictionary(call.Arguments, "fields", out var fields, out var fieldsError))
        {
            return ToolResult.Error(fieldsError!);
        }

        var sessionId = WebToolSessionId.ResolveOrCreate(TryGetOptionalString(call.Arguments, "session_id"), context);
        var request = new WebNavigationRequest(
            SessionId: sessionId,
            Action: action,
            Url: TryGetOptionalString(call.Arguments, "url"),
            LinkIndex: linkIndex,
            LinkText: TryGetOptionalString(call.Arguments, "link_text"),
            LinkSelector: TryGetOptionalString(call.Arguments, "link_selector"),
            FormIndex: formIndex,
            FormSelector: TryGetOptionalString(call.Arguments, "form_selector"),
            Fields: fields);

        WebNavigationResult result;
        try
        {
            result = await _webBrowser.NavigateAsync(request, ct);
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
            action = actionRaw,
            session_id = result.SessionId,
            url = result.Url,
            status_code = result.StatusCode,
            title = result.Title,
            description = result.Description,
            retrieved_at = result.RetrievedAt.ToString("O"),
            links = result.Links.Select(link => new
            {
                index = link.Index,
                text = link.Text,
                href = link.Href,
                resolved_url = link.ResolvedUrl
            }),
            forms = result.Forms.Select(form => new
            {
                index = form.Index,
                method = form.Method,
                action = form.Action,
                resolved_action = form.ResolvedAction,
                fields = form.Fields
            })
        });

        return ToolResult.Success(
            payload,
            ContentTrust.Untrusted,
            details: new Dictionary<string, object?>
            {
                ["observability_event_type"] = "web.navigation",
                ["action"] = actionRaw,
                ["session_id"] = result.SessionId,
                ["url"] = result.Url,
                ["status_code"] = result.StatusCode,
                ["link_count"] = result.Links.Count,
                ["form_count"] = result.Forms.Count
            });
    }

    private static bool TryParseAction(string raw, out WebNavigationAction action)
    {
        action = WebNavigationAction.Open;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "open":
            case "goto":
            case "fetch":
                action = WebNavigationAction.Open;
                return true;
            case "follow_link":
            case "follow":
                action = WebNavigationAction.FollowLink;
                return true;
            case "click":
                action = WebNavigationAction.Click;
                return true;
            case "submit_form":
            case "submit":
                action = WebNavigationAction.SubmitForm;
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetRequiredString(JsonElement arguments, string propertyName, out string value)
    {
        value = string.Empty;
        if (!arguments.TryGetProperty(propertyName, out var node) || node.ValueKind is not JsonValueKind.String)
        {
            return false;
        }

        var raw = node.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        value = raw.Trim();
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

    private static bool TryGetOptionalStringDictionary(
        JsonElement arguments,
        string propertyName,
        out IReadOnlyDictionary<string, string>? values,
        out string? error)
    {
        values = null;
        error = null;

        if (!arguments.TryGetProperty(propertyName, out var node) || node.ValueKind is JsonValueKind.Null)
        {
            return true;
        }

        if (node.ValueKind is not JsonValueKind.Object)
        {
            error = $"{propertyName} must be an object.";
            return false;
        }

        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in node.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                continue;
            }

            parsed[property.Name.Trim()] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => string.Empty,
                _ => property.Value.GetRawText()
            };
        }

        values = parsed;
        return true;
    }
}
