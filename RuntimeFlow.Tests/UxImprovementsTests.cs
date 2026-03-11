using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

public sealed class UxImprovementsTests
{
    #region A. Scope Lifecycle State Tests

    [Fact]
    public void ScopeState_AfterDeclare_IsNotLoaded()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineGlobalScope<TestGlobalScope>();
            builder.DefineSessionScope<TestSessionScope>();
            builder.DefineSceneScope<TestSceneScope>();
            builder.DefineModuleScope<TestModuleScope>();
        });

        Assert.Equal(ScopeLifecycleState.NotLoaded, pipeline.GetScopeState<TestGlobalScope>());
        Assert.Equal(ScopeLifecycleState.NotLoaded, pipeline.GetScopeState<TestSessionScope>());
        Assert.Equal(ScopeLifecycleState.NotLoaded, pipeline.GetScopeState<TestSceneScope>());
        Assert.Equal(ScopeLifecycleState.NotLoaded, pipeline.GetScopeState<TestModuleScope>());
    }

    [Fact]
    public async Task ScopeState_AfterBuildAsync_GlobalAndSessionAreActive()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineGlobalScope<TestGlobalScope>();
            builder.DefineSessionScope<TestSessionScope>();
            builder.For<TestSessionScope>().RegisterInstance<ITestSessionService>(
                new AttemptControlledSessionService((_, _) => Task.CompletedTask));
        });

        await pipeline.InitializeAsync();

        Assert.Equal(ScopeLifecycleState.Active, pipeline.GetScopeState<TestGlobalScope>());
        Assert.Equal(ScopeLifecycleState.Active, pipeline.GetScopeState<TestSessionScope>());
    }

    [Fact]
    public async Task ScopeState_AfterLoadSceneAndModule_AllScopesAreActive()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineGlobalScope<TestGlobalScope>();
            builder.DefineSessionScope<TestSessionScope>();
            builder.DefineSceneScope<TestSceneScope>();
            builder.DefineModuleScope<TestModuleScope>();
            builder.For<TestSessionScope>().RegisterInstance<ITestSessionService>(
                new AttemptControlledSessionService((_, _) => Task.CompletedTask));
            builder.For<TestSceneScope>().RegisterInstance<ITestSceneService>(
                new AttemptControlledSceneService((_, _) => Task.CompletedTask));
            builder.For<TestModuleScope>().RegisterInstance<ITestModuleService>(
                new AttemptControlledModuleService((_, _) => Task.CompletedTask));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadModuleAsync<TestModuleScope>();

        Assert.Equal(ScopeLifecycleState.Active, pipeline.GetScopeState<TestGlobalScope>());
        Assert.Equal(ScopeLifecycleState.Active, pipeline.GetScopeState<TestSessionScope>());
        Assert.Equal(ScopeLifecycleState.Active, pipeline.GetScopeState<TestSceneScope>());
        Assert.Equal(ScopeLifecycleState.Active, pipeline.GetScopeState<TestModuleScope>());
    }

    [Fact]
    public async Task ScopeState_AfterReloadSession_SessionBecomesActive()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope<TestSessionScope>();
            builder.For<TestSessionScope>().RegisterInstance<ITestSessionService>(
                new AttemptControlledSessionService((_, _) => Task.CompletedTask));
        });

        await pipeline.InitializeAsync();
        await pipeline.ReloadScopeAsync<TestSessionScope>();

        Assert.Equal(ScopeLifecycleState.Active, pipeline.GetScopeState<TestSessionScope>());
    }

    [Fact]
    public async Task ScopeState_AfterFailedInit_IsFailed()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope<TestSessionScope>();
            builder.For<TestSessionScope>().RegisterInstance<ITestSessionService>(
                new FailingSessionService());
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.InitializeAsync());

        Assert.Equal(ScopeLifecycleState.Failed, pipeline.GetScopeState<TestSessionScope>());
    }

    [Fact]
    public void ScopeState_UndeclaredScope_ReturnsNotLoaded()
    {
        var pipeline = RuntimePipeline.Create(_ => { });

        Assert.Equal(ScopeLifecycleState.NotLoaded, pipeline.GetScopeState<UndeclaredScope>());
    }

    #endregion

    #region B. Scope State Query API Tests

    [Fact]
    public async Task IsScopeActive_ReturnsTrue_AfterInitialization()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope<TestSessionScope>();
            builder.For<TestSessionScope>().RegisterInstance<ITestSessionService>(
                new AttemptControlledSessionService((_, _) => Task.CompletedTask));
        });

        await pipeline.InitializeAsync();

        Assert.True(pipeline.IsScopeActive<TestSessionScope>());
    }

    [Fact]
    public void IsScopeActive_ReturnsFalse_BeforeInitialization()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope<TestSessionScope>();
        });

        Assert.False(pipeline.IsScopeActive<TestSessionScope>());
    }

    [Fact]
    public async Task CanReloadScope_ReturnsTrue_ForActiveSessionSceneModule()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope<TestSessionScope>();
            builder.DefineSceneScope<TestSceneScope>();
            builder.DefineModuleScope<TestModuleScope>();
            builder.For<TestSessionScope>().RegisterInstance<ITestSessionService>(
                new AttemptControlledSessionService((_, _) => Task.CompletedTask));
            builder.For<TestSceneScope>().RegisterInstance<ITestSceneService>(
                new AttemptControlledSceneService((_, _) => Task.CompletedTask));
            builder.For<TestModuleScope>().RegisterInstance<ITestModuleService>(
                new AttemptControlledModuleService((_, _) => Task.CompletedTask));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadModuleAsync<TestModuleScope>();

        Assert.True(pipeline.CanReloadScope<TestSessionScope>());
        Assert.True(pipeline.CanReloadScope<TestSceneScope>());
        Assert.True(pipeline.CanReloadScope<TestModuleScope>());
    }

    [Fact]
    public async Task CanReloadScope_ReturnsFalse_ForGlobalScope()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineGlobalScope<TestGlobalScope>();
        });

        await pipeline.InitializeAsync();

        Assert.False(pipeline.CanReloadScope<TestGlobalScope>());
    }

    [Fact]
    public void CanReloadScope_ReturnsFalse_ForNonActiveScope()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope<TestSessionScope>();
        });

        Assert.False(pipeline.CanReloadScope<TestSessionScope>());
    }

    [Fact]
    public async Task CanReloadScope_ReturnsFalse_AfterDispose()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope<TestSessionScope>();
            builder.For<TestSessionScope>().RegisterInstance<ITestSessionService>(
                new AttemptControlledSessionService((_, _) => Task.CompletedTask));
        });

        await pipeline.InitializeAsync();
        await pipeline.DisposeAsync();

        Assert.False(pipeline.CanReloadScope<TestSessionScope>());
    }

    #endregion

    #region C. Typed Exception Tests

    [Fact]
    public void ScopeNotDeclaredException_HasCorrectScopeType()
    {
        var exception = new ScopeNotDeclaredException(typeof(UndeclaredScope));

        Assert.Equal(typeof(UndeclaredScope), exception.ScopeType);
        Assert.Contains("UndeclaredScope", exception.Message);
    }

    [Fact]
    public void ScopeNotRestartableException_HasCorrectMessage()
    {
        var exception = new ScopeNotRestartableException(typeof(TestGlobalScope));

        Assert.Equal(typeof(TestGlobalScope), exception.ScopeType);
        Assert.Contains("non-restartable", exception.Message);
    }

    [Fact]
    public void FlowNotConfiguredException_HasCorrectMessage()
    {
        var exception = new FlowNotConfiguredException();

        Assert.Contains("ConfigureFlow", exception.Message);
    }

    [Fact]
    public void ScopeRegistrationException_HasDiagnosticCode()
    {
        var exception = new ScopeRegistrationException("GBSR3001", "Duplicate scope declaration.");

        Assert.Equal("GBSR3001", exception.DiagnosticCode);
        Assert.Contains("Duplicate scope declaration", exception.Message);
    }

    [Fact]
    public async Task ReloadScopeAsync_UndeclaredScope_ThrowsScopeNotDeclaredException()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope<TestSessionScope>();
            builder.For<TestSessionScope>().RegisterInstance<ITestSessionService>(
                new AttemptControlledSessionService((_, _) => Task.CompletedTask));
        });

        await pipeline.InitializeAsync();

        var exception = await Assert.ThrowsAsync<ScopeNotDeclaredException>(() =>
            pipeline.ReloadScopeAsync<UndeclaredScope>());

        Assert.Equal(typeof(UndeclaredScope), exception.ScopeType);
    }

    [Fact]
    public async Task ReloadScopeAsync_GlobalScope_ThrowsScopeNotRestartableException()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineGlobalScope<TestGlobalScope>();
        });

        await pipeline.InitializeAsync();

        var exception = await Assert.ThrowsAsync<ScopeNotRestartableException>(() =>
            pipeline.ReloadScopeAsync<TestGlobalScope>());

        Assert.Equal(typeof(TestGlobalScope), exception.ScopeType);
    }

    [Fact]
    public async Task RunAsync_WithoutConfigureFlow_ThrowsFlowNotConfiguredException()
    {
        var pipeline = RuntimePipeline.Create(_ => { });

        await Assert.ThrowsAsync<FlowNotConfiguredException>(() =>
            pipeline.RunAsync(NoopSceneLoader.Instance));
    }

    #endregion

    #region D. Pipeline Presets Tests

    [Fact]
    public void Presets_Minimal_DoesNotThrow()
    {
        var exception = Record.Exception(() =>
            RuntimePipeline.Create(_ => { }, RuntimePipelinePresets.Minimal));

        Assert.Null(exception);
    }

    [Fact]
    public void Presets_Development_DisablesHealth()
    {
        var options = new RuntimePipelineOptions();
        RuntimePipelinePresets.Development(options);

        Assert.False(options.Health.Enabled);
    }

    [Fact]
    public void Presets_Production_EnablesHealthWithExpectedDefaults()
    {
        var options = new RuntimePipelineOptions();
        RuntimePipelinePresets.Production(options);

        Assert.True(options.Health.Enabled);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.Health.MinimumExpectedServiceDuration);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Health.MinimumServiceTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Health.MaximumServiceTimeout);
        Assert.Equal(2.0, options.Health.SlowServiceMultiplier);
        Assert.Equal(1, options.Health.MaxAutoSessionRestartsPerRun);
    }

    [Fact]
    public void Presets_AllCanBeUsedWithRuntimePipelineCreate()
    {
        Assert.Null(Record.Exception(() => RuntimePipeline.Create(_ => { }, RuntimePipelinePresets.Minimal)));
        Assert.Null(Record.Exception(() => RuntimePipeline.Create(_ => { }, RuntimePipelinePresets.Development)));
        Assert.Null(Record.Exception(() => RuntimePipeline.Create(_ => { }, RuntimePipelinePresets.Production)));
    }

    #endregion

    #region E. IAsyncDisposable Tests

    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        var pipeline = RuntimePipeline.Create(_ => { });

        await pipeline.DisposeAsync();
        var exception = await Record.ExceptionAsync(async () => await pipeline.DisposeAsync());

        Assert.Null(exception);
    }

    [Fact]
    public async Task AfterDispose_InitializeAsync_ThrowsObjectDisposedException()
    {
        var pipeline = RuntimePipeline.Create(_ => { });
        await pipeline.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pipeline.InitializeAsync());
    }

    [Fact]
    public async Task AfterDispose_RunAsync_ThrowsObjectDisposedException()
    {
        var pipeline = RuntimePipeline.Create(_ => { })
            .ConfigureFlow(new DelegateRuntimeFlowScenario((_, _) => Task.CompletedTask));
        await pipeline.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            pipeline.RunAsync(NoopSceneLoader.Instance));
    }

    [Fact]
    public async Task AfterDispose_LoadSceneAsync_ThrowsObjectDisposedException()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSceneScope<TestSceneScope>();
        });
        await pipeline.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            pipeline.LoadSceneAsync<TestSceneScope>());
    }

    #endregion

    #region F. Operation Codes Tests

    [Fact]
    public void OperationCodes_AllConstantsAreNonNullNonEmpty()
    {
        var fields = typeof(RuntimeOperationCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string));

        foreach (var field in fields)
        {
            var value = (string?)field.GetValue(null);
            Assert.False(string.IsNullOrEmpty(value),
                $"Operation code '{field.Name}' must be non-null and non-empty.");
        }
    }

    [Fact]
    public void OperationCodes_AllConstantsHaveUniqueValues()
    {
        var values = typeof(RuntimeOperationCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string?)f.GetValue(null))
            .ToArray();

        Assert.Equal(values.Length, values.Distinct().Count());
    }

    #endregion

    #region Test Helpers

    private sealed class UndeclaredScope { }

    private sealed class FailingSessionService : ITestSessionService
    {
        public int Attempts => 0;

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Simulated init failure");
        }
    }

    #endregion
}
