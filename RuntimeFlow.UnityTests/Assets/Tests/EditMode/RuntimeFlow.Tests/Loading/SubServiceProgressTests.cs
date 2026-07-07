using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{

public sealed class SubServiceProgressTests
{
    [Test]
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

        Assert.That(called, Is.True, "InitializeAsync(context, ct) should have been called");
    }

    [Test]
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

        Assert.That(recorder.ServiceProgressCalls, Has.Count.EqualTo(1));
        var call = recorder.ServiceProgressCalls[0];
        Assert.That(call.Scope, Is.EqualTo(GameContextType.Session));
        Assert.That(call.ServiceType, Is.EqualTo(typeof(ITestProgressAwareSessionService)));
        Assert.That(call.Progress, Is.EqualTo(0.5f));
        Assert.That(call.Message, Is.EqualTo("halfway"));
    }

    [Test]
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

        Assert.That(called, Is.True, "InitializeAsync(ct) should have been called for non-progress-aware services");
    }

    [Test]
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

        Assert.That(recorder.ServiceProgressCalls, Has.Count.EqualTo(2));
        Assert.That(recorder.ServiceProgressCalls[0].Progress, Is.EqualTo(0f));
        Assert.That(recorder.ServiceProgressCalls[1].Progress, Is.EqualTo(1f));
    }

    #region Test Helpers

    private interface ITestProgressAwareSessionService : ISessionInitializableService
    {
    }

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

    private interface ITestNonProgressAwareSessionService : ISessionInitializableService
    {
    }

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

    private sealed class ServiceProgressCall
    {
        public ServiceProgressCall(
            GameContextType scope,
            Type serviceType,
            float progress,
            string? message,
            int completedServices,
            int totalServices)
        {
            Scope = scope;
            ServiceType = serviceType;
            Progress = progress;
            Message = message;
            CompletedServices = completedServices;
            TotalServices = totalServices;
        }

        public GameContextType Scope { get; }
        public Type ServiceType { get; }
        public float Progress { get; }
        public string? Message { get; }
        public int CompletedServices { get; }
        public int TotalServices { get; }
    }

    #endregion
}
}
