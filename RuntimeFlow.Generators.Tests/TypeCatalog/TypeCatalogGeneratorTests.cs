using Microsoft.CodeAnalysis;

namespace RuntimeFlow.Generators.Tests.TypeCatalog;

/// <summary>
/// The runtime discovery trusts the catalog instead of scanning the assembly, so a type missing from it is a service
/// that silently never initializes, and a catalog that doesn't compile breaks the user's build.
/// </summary>
public sealed class TypeCatalogGeneratorTests
{
    private const string Source = """
        using RuntimeFlow.Contexts;

        namespace Game
        {
            public interface IEntryPoint { }
            public interface IGameEntryPoint : IEntryPoint { }
            public interface IStandalone { }
            internal interface IRepository<T> : System.IDisposable { }

            public interface IMyService : ISessionInitializableService { }
            public class MyService : IMyService { }
            public class Plain { }

            public class Outer<T>
            {
                internal interface INested<TA, TB> : IEntryPoint { }
                internal class NestedService : IAsyncInitializableService { }
                private class HiddenService : IAsyncInitializableService { }
            }
        }
        """;

    [Test]
    public void Catalog_ListsMetadataNamesOfContractTypesAndDerivedInterfaces_AndCompiles()
    {
        var (_, sources, output) = GeneratorTestHost.Run(new TypeCatalogGenerator(), GeneratorTestHost.CommonStubs, Source);

        var catalog = sources["RuntimeFlowTypeCatalog.g.cs"];
        Assert.That(output.Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, catalog);
        Assert.That(catalog, Does.Contain("\"Game.MyService\""));
        Assert.That(catalog, Does.Contain("\"Game.IMyService\""));
        Assert.That(catalog, Does.Contain("\"Game.Outer`1+NestedService\""));
        Assert.That(catalog, Does.Contain("\"Game.Outer`1+HiddenService\""));
        Assert.That(catalog, Does.Contain("\"Game.IGameEntryPoint\""));
        Assert.That(catalog, Does.Contain("\"Game.IRepository`1\""));
        Assert.That(catalog, Does.Contain("\"Game.Outer`1+INested`2\""));
        Assert.That(catalog, Does.Not.Contain("Plain"));
        Assert.That(catalog, Does.Not.Contain("IStandalone"));
    }

    [Test]
    public void AssemblyWithoutRuntimeFlow_GetsNoCatalog()
    {
        var (_, sources, _) = GeneratorTestHost.Run(new TypeCatalogGenerator(), "namespace Game { public interface IA : System.IDisposable { } }");

        Assert.That(sources, Is.Empty);
    }
}
