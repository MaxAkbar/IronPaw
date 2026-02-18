using System.Reflection;
using System.Runtime.Loader;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

public static class SkillAddinLoader
{
    public static SkillAddinLoadResult LoadFromDirectories(IEnumerable<string> directories)
    {
        var tools = new List<ITool>();
        var loadedSkills = new List<LoadedSkillInfo>();
        var diagnostics = new List<string>();
        var usedToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var fullDirectory = Path.GetFullPath(directory);
            if (!Directory.Exists(fullDirectory))
            {
                continue;
            }

            foreach (var assemblyPath in Directory.EnumerateFiles(fullDirectory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                TryLoadAssemblySkills(assemblyPath, usedToolNames, tools, loadedSkills, diagnostics);
            }
        }

        return new SkillAddinLoadResult(tools, loadedSkills, diagnostics);
    }

    private static void TryLoadAssemblySkills(
        string assemblyPath,
        HashSet<string> usedToolNames,
        List<ITool> tools,
        List<LoadedSkillInfo> loadedSkills,
        List<string> diagnostics)
    {
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
            foreach (var addinType in FindAddinTypes(assembly))
            {
                if (!TryCreateAddin(addinType, out var addin, out var error))
                {
                    diagnostics.Add($"Failed to create add-in type '{addinType.FullName}' from '{assemblyPath}': {error}");
                    continue;
                }

                LoadAddinSkills(addin!, assemblyPath, usedToolNames, tools, loadedSkills, diagnostics);
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add($"Failed to load assembly '{assemblyPath}': {ex.Message}");
        }
    }

    private static IEnumerable<Type> FindAddinTypes(Assembly assembly)
    {
        try
        {
            return assembly
                .GetTypes()
                .Where(type =>
                    !type.IsAbstract &&
                    !type.IsInterface &&
                    typeof(ISkillAddin).IsAssignableFrom(type))
                .ToArray();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types
                .Where(type => type is not null)
                .Cast<Type>()
                .Where(type =>
                    !type.IsAbstract &&
                    !type.IsInterface &&
                    typeof(ISkillAddin).IsAssignableFrom(type))
                .ToArray();
        }
    }

    private static bool TryCreateAddin(Type addinType, out ISkillAddin? addin, out string? error)
    {
        addin = null;
        error = null;

        try
        {
            if (Activator.CreateInstance(addinType) is not ISkillAddin instance)
            {
                error = "Type does not implement ISkillAddin.";
                return false;
            }

            addin = instance;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void LoadAddinSkills(
        ISkillAddin addin,
        string assemblyPath,
        HashSet<string> usedToolNames,
        List<ITool> tools,
        List<LoadedSkillInfo> loadedSkills,
        List<string> diagnostics)
    {
        IReadOnlyList<ISkill> skills;
        try
        {
            skills = addin.CreateSkills() ?? [];
        }
        catch (Exception ex)
        {
            diagnostics.Add($"Add-in '{addin.AddinId}' failed to create skills: {ex.Message}");
            return;
        }

        foreach (var skill in skills)
        {
            var requestedToolName = SkillToolAdapter.BuildToolName(skill.Descriptor.Name);
            var toolName = EnsureUniqueToolName(requestedToolName, usedToolNames);
            var tool = new SkillToolAdapter(skill, addin.AddinId, toolName);
            tools.Add(tool);
            loadedSkills.Add(new LoadedSkillInfo(
                addin.AddinId,
                skill.Descriptor.Name,
                toolName,
                skill.Descriptor.Description));
        }

        diagnostics.Add($"Loaded add-in '{addin.AddinId}' from '{assemblyPath}' with {skills.Count} skill(s).");
    }

    private static string EnsureUniqueToolName(string baseName, HashSet<string> usedToolNames)
    {
        if (usedToolNames.Add(baseName))
        {
            return baseName;
        }

        var index = 2;
        while (true)
        {
            var candidate = $"{baseName}_{index}";
            if (usedToolNames.Add(candidate))
            {
                return candidate;
            }

            index++;
        }
    }
}
