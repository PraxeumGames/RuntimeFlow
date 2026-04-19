using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RuntimeFlow.Generators;

namespace RuntimeFlow.Generators.Tests;

public static class GeneratorTestHost
{
    /// <summary>
    /// Minimal stubs matching the real package's namespaces so the generator's symbol matching works.
    /// </summary>
    public const string CommonStubs = """
        namespace RuntimeFlow.Contexts
        {
            public interface IAsyncInitializableService { }
            public interface IGlobalInitializableService  : IAsyncInitializableService { }
            public interface ISessionInitializableService : IAsyncInitializableService { }
            public interface ISceneInitializableService   : IAsyncInitializableService { }
            public interface IModuleInitializableService  : IAsyncInitializableService { }
        }
        """;

    /// <summary>
    /// Compiles <paramref name="sources"/> into a CSharpCompilation, runs
    /// <see cref="InitializationGraphGenerator"/> over it, and returns the
    /// RF-prefixed diagnostics together with the generated source files.
    /// </summary>
    public static (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyDictionary<string, string> GeneratedSources)
        RunGenerator(params string[] sources)
    {
        var syntaxTrees = sources
            .Select(src => CSharpSyntaxTree.ParseText(src,
                new CSharpParseOptions(LanguageVersion.Latest)))
            .ToArray();

        var references = BuildReferences();

        var compilation = CSharpCompilation.Create(
            assemblyName: "GeneratorTestAssembly",
            syntaxTrees: syntaxTrees,
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new InitializationGraphGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        var runResult = driver.GetRunResult();

        var rfDiagnostics = runResult.Diagnostics
            .Where(d => d.Id.StartsWith("RF", StringComparison.Ordinal))
            .ToList();

        var generatedSources = runResult.GeneratedTrees
            .ToDictionary(
                tree => Path.GetFileName(tree.FilePath),
                tree => tree.GetText().ToString());

        return (rfDiagnostics, generatedSources);
    }

    private static IEnumerable<MetadataReference> BuildReferences()
    {
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        return tpa
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Cast<MetadataReference>();
    }
}
