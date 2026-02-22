using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools;

internal sealed class RoslynSkillCompiler
{
    private readonly IReadOnlyList<MetadataReference> _baseReferences;

    public RoslynSkillCompiler()
    {
        _baseReferences = BuildBaseReferences();
    }

    public RoslynCompilationResult Compile(string sourceCode, string assemblyName)
    {
        var compilation = CreateCompilation(sourceCode, assemblyName);
        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);

        if (!emitResult.Success)
        {
            return RoslynCompilationResult.Failure(GetErrors(emitResult.Diagnostics));
        }

        ms.Seek(0, SeekOrigin.Begin);
        var assembly = AssemblyLoadContext.Default.LoadFromStream(ms);
        return RoslynCompilationResult.Success(assembly);
    }

    public RoslynCompilationResult CompileToFile(string sourceCode, string assemblyPath)
    {
        var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
        var compilation = CreateCompilation(sourceCode, assemblyName);

        var directory = Path.GetDirectoryName(assemblyPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var emitResult = compilation.Emit(assemblyPath);
        if (!emitResult.Success)
        {
            return RoslynCompilationResult.Failure(GetErrors(emitResult.Diagnostics));
        }

        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        return RoslynCompilationResult.Success(assembly);
    }

    private CSharpCompilation CreateCompilation(string sourceCode, string assemblyName)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            sourceCode,
            new CSharpParseOptions(LanguageVersion.Latest));

        return CSharpCompilation.Create(
            assemblyName,
            syntaxTrees: [syntaxTree],
            references: _baseReferences,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false));
    }

    private static IReadOnlyList<string> GetErrors(IEnumerable<Diagnostic> diagnostics)
    {
        return diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();
    }

    private static List<MetadataReference> BuildBaseReferences()
    {
        var references = new List<MetadataReference>();

        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrEmpty(trustedAssemblies))
        {
            foreach (var path in trustedAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var fileName = Path.GetFileName(path);
                if (fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                    fileName.StartsWith("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase))
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
            }
        }

        var abstractionsLocation = typeof(ISkillAddin).Assembly.Location;
        if (!string.IsNullOrEmpty(abstractionsLocation))
        {
            references.Add(MetadataReference.CreateFromFile(abstractionsLocation));
        }

        return references;
    }
}

internal sealed class RoslynCompilationResult
{
    public bool IsSuccess { get; private init; }
    public Assembly? Assembly { get; private init; }
    public IReadOnlyList<string> Errors { get; private init; } = [];

    public static RoslynCompilationResult Success(Assembly assembly)
        => new() { IsSuccess = true, Assembly = assembly };

    public static RoslynCompilationResult Failure(IReadOnlyList<string> errors)
        => new() { IsSuccess = false, Errors = errors };
}
