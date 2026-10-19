using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public static class ToolPolicyEvaluator
{
    private static readonly IReadOnlyDictionary<ToolProfile, IReadOnlySet<string>> GroupProfiles =
        new Dictionary<ToolProfile, IReadOnlySet<string>>
        {
            [ToolProfile.Minimal] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "group:system"
            },
            [ToolProfile.Coding] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "group:fs",
                "group:system",
                "group:skills",
                "group:agents"
            },
            [ToolProfile.Messaging] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "group:messaging",
                "group:system",
                "group:skills"
            },
            [ToolProfile.Web] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "group:web",
                "group:messaging",
                "group:system"
            },
            [ToolProfile.Full] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "group:fs",
                "group:runtime",
                "group:web",
                "group:messaging",
                "group:system",
                "group:skills",
                "group:agents"
            }
        };

    public static IReadOnlyList<ITool> Apply(IEnumerable<ITool> tools, ToolSelectionPolicy policy)
    {
        var profileGroups = GroupProfiles[policy.Profile];
        var allow = ToLookup(policy.AllowList);
        var deny = ToLookup(policy.DenyList);
        var explicitGroups = ToLookup(policy.AllowedGroups);
        var mcpTools = ToLookup(policy.AllowedMcpTools);
        var filtered = new List<ITool>();

        foreach (var tool in tools)
        {
            if (tool.Descriptor.OwnerOnly && !policy.SenderIsOwner)
            {
                continue;
            }

            if (deny.Contains(tool.Descriptor.Name))
            {
                continue;
            }

            if (allow.Count > 0 && !allow.Contains(tool.Descriptor.Name))
            {
                continue;
            }

            var isMcp = tool.Descriptor.Group.Equals("group:mcp", StringComparison.OrdinalIgnoreCase);
            if (isMcp
                ? !policy.SenderIsOwner || policy.Profile is not (ToolProfile.Coding or ToolProfile.Full) || !mcpTools.Contains(tool.Descriptor.Name)
                : !profileGroups.Contains(tool.Descriptor.Group))
            {
                continue;
            }

            if (explicitGroups.Count > 0 && !explicitGroups.Contains(tool.Descriptor.Group))
            {
                continue;
            }

            if (policy.SubagentDepth > 0 && tool.Descriptor.Group.Equals("group:runtime", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            filtered.Add(tool);
        }

        return policy.InheritedPolicy is null
            ? filtered
            : Apply(filtered, policy.InheritedPolicy with
            {
                SenderIsOwner = policy.SenderIsOwner,
                SubagentDepth = Math.Max(policy.SubagentDepth, policy.InheritedPolicy.SubagentDepth)
            });
    }

    private static HashSet<string> ToLookup(IReadOnlyCollection<string>? values)
    {
        return values is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
    }
}
