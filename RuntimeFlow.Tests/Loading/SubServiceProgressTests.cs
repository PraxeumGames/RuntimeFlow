using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

public sealed class SubServiceProgressTests
{
    [Fact]
    public async Task ProgressAwareService_ReceivesContext()
    {
        var called = false;
        var service = new TestProgressAwareSessionService((ctx, ct) =>
        {
            called = true;
            return Task.CompletedTask;
        });

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<ITestProgressAwareSessionService>(service);
        });

        await pipeline.InitializeAsync();

        Assert.True(called, "InitializeAsync(context, ct) should have been called");
    }

    [Fact]
    public async Task ProgressNotifier_ReceivesSubProgress()
    {
        var recorder = new ProgressRecorder();
        var service = new TestProgressAwareSessionService((ctx, ct) =>
        {
            ctx.ReportProgress(0.5f, "halfway");
            return Task.CompletedTask;
        });

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<ITestProgressAwareSessionService>(service);
        });

        await pipeline.InitializeAsync(recorder);

        Assert.Single(recorder.ServiceProgressCalls);
        var call = recorder.ServiceProgressCalls[0];
        Assert.Equal(GameContextType.Session, call.Scope);
        Assert.Equal(typeof(ITestProgressAwareSessionService), call.ServiceType);
        Assert.Equal(0.5f, call.Progress);
        Assert.Equal("halfway", call.Message);
    }

    [Fact]
    public async Task NonProgressAwareService_StillWorksViaNormalPath()
    {
        var called = false;
        var service = new TestNonProgressAwareSessionService(() =>
        {
            called = true;
            return Task.CompletedTask;
        });

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<ITestNonProgressAwareSessionService>(service);
        });

        await pipeline.InitializeAsync();

        Assert.True(called, "InitializeAsync(ct) should have been called for non-progress-aware services");
    }

    [Fact]
    public async Task ProgressValues_AreClamped()
    {
        var recorder = new ProgressRecorder();
        var service = new TestProgressAwareSessionService((ctx, ct) =>
        {
            ctx.ReportProgress(-0.5f, "underflow");
            ctx.ReportProgress(1.5f, "overflow");
            return Task.CompletedTask;
        });

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<ITestProgressAwareSessionService>(service);
        });

        await pipeline.InitializeAsync(recorder);

        Assert.Equal(2, recorder.ServiceProgressCalls.Count);
        Assert.Equal(0f, recorder.ServiceProgressCalls[0].Progress);
        Assert.Equal(1f, recorder.ServiceProgressCalls[1].Progress);
    }

    #region Test Helpers

    private interface ITestProgressAwareSessionService : ISessionInitializableService;

    private sealed class TestProgressAwareSessionService : ITestProgressAwareSessionService, IProgressAwareInitializableService
    {
        private readonly Func<IServiceInitializationContext, CancellationToken, Task> _behavior;

        public TestProgressAwareSessionService(Func<IServiceInitializationContext, CancellationToken, Task> behavior)
        {
            _behavior = behavior;
        }

        public Task InitializeAsync(IServiceInitializationContext context, CancellationToken cancellationToken)
            => _behavior(context, cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("Should not be called for progress-aware services");
    }

    private interface ITestNonProgressAwareSessionService : ISessionInitializableService;

    private sealed class TestNonProgressAwareSessionService : ITestNonProgressAwareSessionService
    {
        private readonly Func<Task> _behavior;

        public TestNonProgressAwareSessionService(Func<Task> behavior) => _behavior = behavior;

        public Task InitializeAsync(CancellationToken cancellationToken) => _behavior();
    }

    private sealed class ProgressRecorder : IInitializationProgressNotifier
    {
        public List<ServiceProgressCall> ServiceProgressCalls { get; } = new();

        public void OnScopeStarted(GameContextType scope, int totalServices) { }
        public void OnServiceStarted(GameContextType scope, Type serviceType, int completedServices, int totalServices) { }
        public void OnServiceCompleted(GameContextType scope, Type serviceType, int completedServices, int totalServices) { }
        public void OnScopeCompleted(GameContextType scope, int totalServices) { }

        public void OnServiceProgress(GameContextType scope, Type serviceType, float progress, string? message, int completedServices, int totalServices)
        {
            ServiceProgressCalls.Add(new ServiceProgressCall(scope, serviceType, progress, message, completedServices, totalServices));
        }

        public Task OnGlobalContextReadyForSessionInitializationAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task OnSessionRestartTeardownCompletedAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private record ServiceProgressCall(
        GameContextType Scope,
        Type ServiceType,
        float Progress,
        string? Message,
        int CompletedServices,
        int TotalServices);

    #endregion
}
