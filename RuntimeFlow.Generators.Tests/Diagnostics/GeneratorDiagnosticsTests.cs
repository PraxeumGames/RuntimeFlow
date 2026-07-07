namespace RuntimeFlow.Generators.Tests.Diagnostics;

/// <summary>
/// End-to-end tests for InitializationGraphGenerator running via CSharpGeneratorDriver.
/// Each test compiles a small C# snippet (plus CommonStubs) and asserts on the
/// RF-prefixed diagnostics or the generated CompiledInitializationGraph.g.cs content.
/// </summary>
public sealed class GeneratorDiagnosticsTests
{
    // -------------------------------------------------------------------------
    // RF0001 – Duplicate implementation
    // -------------------------------------------------------------------------

    [Test]
    public void RF0001_DuplicateImplementation_WhenTwoClassesImplementSameContract()
    {
        const string source = """
            using RuntimeFlow.Contexts;

            public interface IMyGlobalService : IGlobalInitializableService { }
            public class ImplementationA : IMyGlobalService { }
            public class ImplementationB : IMyGlobalService { }
            """;

        var (diagnostics, _) = GeneratorTestHost.RunGenerator(GeneratorTestHost.CommonStubs, source);

        var rf0001 = diagnostics.Where(d => d.Id == "RF0001").ToList();
        Assert.That(rf0001, Has.Count.EqualTo(1));
        Assert.That(rf0001[0].GetMessage(), Does.Contain("ImplementationA"));
        Assert.That(rf0001[0].GetMessage(), Does.Contain("ImplementationB"));
    }

    // -------------------------------------------------------------------------
    // RF0002 – Missing dependency
    // -------------------------------------------------------------------------

    [Test]
    public void RF0002_MissingDependency_WhenConstructorTakesUnregisteredAsyncService()
    {
        const string source = """
            using RuntimeFlow.Contexts;

            public interface IMyGlobalService : IGlobalInitializableService { }

            // IGhostService is a valid scoped-async contract but has no implementation class.
            public interface IGhostService : IGlobalInitializableService { }

            public class MyGlobalService : IMyGlobalService
            {
                public MyGlobalService(IGhostService ghost) { }
            }
            """;

        var (diagnostics, _) = GeneratorTestHost.RunGenerator(GeneratorTestHost.CommonStubs, source);

        var rf0002 = diagnostics.Where(d => d.Id == "RF0002").ToList();
        Assert.That(rf0002, Has.Count.EqualTo(1));
        Assert.That(rf0002[0].GetMessage(), Does.Contain("IGhostService"));
    }

    // -------------------------------------------------------------------------
    // RF0003 – Scope violation
    // -------------------------------------------------------------------------

    [Test]
    public void RF0003_ScopeViolation_WhenSceneScopedServiceDependsOnModuleScopedService()
    {
        // Scene (scope=2) → Module (scope=3): Module has a higher scope value
        // so (int)dependencyNode.Scope > (int)node.Scope evaluates to 3 > 2 = true → violation.
        const string source = """
            using RuntimeFlow.Contexts;

            public interface IMySceneService  : ISceneInitializableService  { }
            public interface IMyModuleService : IModuleInitializableService { }

            public class ModuleService : IMyModuleService { }

            public class SceneService : IMySceneService
            {
                public SceneService(IMyModuleService dep) { }
            }
            """;

        var (diagnostics, _) = GeneratorTestHost.RunGenerator(GeneratorTestHost.CommonStubs, source);

        var rf0003 = diagnostics.Where(d => d.Id == "RF0003").ToList();
        Assert.That(rf0003, Has.Count.EqualTo(1));

        var message = rf0003[0].GetMessage();
        Assert.That(message, Does.Contain("Scene"));
        Assert.That(message, Does.Contain("Module"));
    }

    // -------------------------------------------------------------------------
    // RF0004 – Circular dependency
    // -------------------------------------------------------------------------

    [Test]
    public void RF0004_Cycle_WhenTwoServicesFormACircularDependency()
    {
        const string source = """
            using RuntimeFlow.Contexts;

            public interface IServiceA : IGlobalInitializableService { }
            public interface IServiceB : IGlobalInitializableService { }

            public class ServiceA : IServiceA
            {
                public ServiceA(IServiceB b) { }
            }

            public class ServiceB : IServiceB
            {
                public ServiceB(IServiceA a) { }
            }
            """;

        var (diagnostics, _) = GeneratorTestHost.RunGenerator(GeneratorTestHost.CommonStubs, source);

        var rf0004 = diagnostics.Where(d => d.Id == "RF0004").ToList();
        Assert.That(rf0004, Has.Count.EqualTo(1));

        var message = rf0004[0].GetMessage();
        // The cycle message contains both service type names.
        Assert.That(message, Does.Contain("IServiceA"));
        Assert.That(message, Does.Contain("IServiceB"));
    }

    // -------------------------------------------------------------------------
    // Happy path – no diagnostics, graph file generated
    // -------------------------------------------------------------------------

    [Test]
    public void HappyPath_NoDiagnostics_GeneratesGraph()
    {
        const string source = """
            using RuntimeFlow.Contexts;

            public interface IGlobalSvc  : IGlobalInitializableService  { }
            public interface ISessionSvc : ISessionInitializableService { }

            public class GlobalSvc : IGlobalSvc { }

            public class SessionSvc : ISessionSvc
            {
                // Depends on global service (legal: Session can depend on Global).
                public SessionSvc(IGlobalSvc g) { }
            }
            """;

        var (diagnostics, generatedSources) = GeneratorTestHost.RunGenerator(GeneratorTestHost.CommonStubs, source);

        // No RF diagnostics.
        Assert.That(diagnostics.Any(d => d.Id.StartsWith("RF")), Is.False);

        // Generated file must exist.
        Assert.That(
            generatedSources.ContainsKey("CompiledInitializationGraph.g.cs"),
            Is.True,
            "Expected CompiledInitializationGraph.g.cs to be generated.");

        var content = generatedSources["CompiledInitializationGraph.g.cs"];
        Assert.That(content, Does.Contain("GlobalSvc"));
        Assert.That(content, Does.Contain("SessionSvc"));
    }

    [Test]
    public void StageMarkerOnlyImplementations_DoNotBecomeGraphServiceContracts()
    {
        const string source = """
            using RuntimeFlow.Contexts;

            public class PlatformMarkerOnlyA : IPlatformStartupInitializableService { }
            public class PlatformMarkerOnlyB : IPlatformStartupInitializableService { }
            """;

        var (diagnostics, generatedSources) = GeneratorTestHost.RunGenerator(GeneratorTestHost.CommonStubs, source);

        Assert.That(diagnostics.Any(d => d.Id.StartsWith("RF")), Is.False);

        var content = generatedSources["CompiledInitializationGraph.g.cs"];
        Assert.That(content, Does.Not.Contain("PlatformMarkerOnlyA"));
        Assert.That(content, Does.Not.Contain("PlatformMarkerOnlyB"));
        Assert.That(content, Does.Not.Contain("IPlatformStartupInitializableService"));
    }

    [Test]
    public void UserStageServiceContract_GeneratesSessionScopedNode()
    {
        const string source = """
            using RuntimeFlow.Contexts;

            public interface IMyPlatformService : IPlatformStartupInitializableService { }

            public class MyPlatformService : IMyPlatformService { }
            """;

        var (diagnostics, generatedSources) = GeneratorTestHost.RunGenerator(GeneratorTestHost.CommonStubs, source);

        Assert.That(diagnostics.Any(d => d.Id.StartsWith("RF")), Is.False);

        var content = generatedSources["CompiledInitializationGraph.g.cs"];
        Assert.That(content, Does.Contain("IMyPlatformService"));
        Assert.That(content, Does.Contain("MyPlatformService"));
        Assert.That(content, Does.Contain("GameContextType.Session"));
    }
}
