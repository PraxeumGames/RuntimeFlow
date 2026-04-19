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

    [Fact]
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
        Assert.Single(rf0001);
        Assert.Contains("ImplementationA", rf0001[0].GetMessage());
        Assert.Contains("ImplementationB", rf0001[0].GetMessage());
    }

    // -------------------------------------------------------------------------
    // RF0002 – Missing dependency
    // -------------------------------------------------------------------------

    [Fact]
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
        Assert.Single(rf0002);
        Assert.Contains("IGhostService", rf0002[0].GetMessage());
    }

    // -------------------------------------------------------------------------
    // RF0003 – Scope violation
    // -------------------------------------------------------------------------

    [Fact]
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
        Assert.Single(rf0003);

        var message = rf0003[0].GetMessage();
        Assert.Contains("Scene", message);
        Assert.Contains("Module", message);
    }

    // -------------------------------------------------------------------------
    // RF0004 – Circular dependency
    // -------------------------------------------------------------------------

    [Fact]
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
        Assert.Single(rf0004);

        var message = rf0004[0].GetMessage();
        // The cycle message contains both service type names.
        Assert.Contains("IServiceA", message);
        Assert.Contains("IServiceB", message);
    }

    // -------------------------------------------------------------------------
    // Happy path – no diagnostics, graph file generated
    // -------------------------------------------------------------------------

    [Fact]
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
        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("RF"));

        // Generated file must exist.
        Assert.True(
            generatedSources.ContainsKey("CompiledInitializationGraph.g.cs"),
            "Expected CompiledInitializationGraph.g.cs to be generated.");

        var content = generatedSources["CompiledInitializationGraph.g.cs"];
        Assert.Contains("GlobalSvc", content);
        Assert.Contains("SessionSvc", content);
    }
}
