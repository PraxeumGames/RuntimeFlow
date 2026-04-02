using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

public sealed class ServiceDisposalLifecycleTests
{
    [Fact]
    public async Task Services_AreDisposedInReverseOrder()
    {
        var calls = new List<string>();
        var serviceC = new DisposableSessionServiceC(calls);
        var serviceB = new DisposableSessionServiceB(calls, serviceC);
        var serviceA = new DisposableSessionServiceA(calls, serviceB);

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<IDisposableSessionServiceC>(serviceC);
            builder.Session().RegisterInstance<IDisposableSessionServiceB>(serviceB);
            builder.Session().RegisterInstance<IDisposableSessionServiceA>(serviceA);
        });

        await pipeline.InitializeAsync();
        calls.Clear();
        await pipeline.RestartSessionAsync();

        var disposalCalls = calls.FindAll(c => c.StartsWith("dispose:"));
        Assert.Equal(
            new[] { "dispose:A", "dispose:B", "dispose:C" },
            disposalCalls);
    }

    [Fact]
    public async Task Disposal_ContinuesAfterIndividualFailure()
    {
        var calls = new List<string>();
        var serviceOk = new DisposableSessionServiceOk(calls);
        var serviceFailing = new DisposableSessionServiceFailing(calls);

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<IDisposableSessionServiceOk>(serviceOk);
            builder.Session().RegisterInstance<IDisposableSessionServiceFailing>(serviceFailing);
        });

        await pipeline.InitializeAsync();
        calls.Clear();

        var ex = await Assert.ThrowsAsync<AggregateException>(() => pipeline.RestartSessionAsync());
        Assert.Single(ex.InnerExceptions);

        Assert.Contains("dispose:failing", calls);
        Assert.Contains("dispose:ok", calls);
    }

    [Fact]
    public async Task SceneServices_DisposedOnSceneReload()
    {
        var calls = new List<string>();
        var sceneService = new DisposableSceneService(calls);

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new TestSceneScope(s => s.RegisterInstance<IDisposableSceneService>(sceneService)));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        calls.Clear();
        await pipeline.ReloadScopeAsync<TestSceneScope>();

        var disposalCalls = calls.FindAll(c => c.StartsWith("dispose:"));
        Assert.Equal(new[] { "dispose:scene" }, disposalCalls);
    }

    [Fact]
    public async Task LoadScene_WhenInitializationAndCleanupBothFail_AggregatesBothErrors()
    {
        var failingService = new FailingInitAndDisposeSceneService();
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new TestSceneScope(s => s.RegisterInstance<IFailingInitAndDisposeSceneService>(failingService)));
        });

        await pipeline.InitializeAsync();

        var exception = await Assert.ThrowsAsync<AggregateException>(() => pipeline.LoadSceneAsync<TestSceneScope>());
        Assert.Contains(
            exception.InnerExceptions,
            inner => inner is InvalidOperationException invalid
                     && invalid.Message == "scene init failed");
        Assert.Contains(
            exception.InnerExceptions,
            inner => inner is InvalidOperationException invalid
                     && invalid.Message == "scene dispose failed");
        Assert.Equal(RuntimeExecutionState.Failed, pipeline.GetRuntimeStatus().State);
    }

    // --- Service contracts ---
    private interface IDisposableSessionServiceA : ISessionInitializableService, ISessionDisposableService { }
    private interface IDisposableSessionServiceB : ISessionInitializableService, ISessionDisposableService { }
    private interface IDisposableSessionServiceC : ISessionInitializableService, ISessionDisposableService { }
    private interface IDisposableSessionServiceOk : ISessionInitializableService, ISessionDisposableService { }
    private interface IDisposableSessionServiceFailing : ISessionInitializableService, ISessionDisposableService { }
    private interface IDisposableSceneService : ISceneInitializableService, ISceneDisposableService { }
    private interface IFailingInitAndDisposeSceneService : ISceneInitializableService { }

    // --- Service implementations ---
    // C has no dependencies (initialized first)
    private sealed class DisposableSessionServiceC : IDisposableSessionServiceC
    {
        private readonly List<string> _calls;
        public DisposableSessionServiceC(List<string> calls) => _calls = calls;
        public Task InitializeAsync(CancellationToken cancellationToken) { _calls.Add("init:C"); return Task.CompletedTask; }
        public Task DisposeAsync(CancellationToken cancellationToken) { _calls.Add("dispose:C"); return Task.CompletedTask; }
    }

    // B depends on C (initialized second)
    private sealed class DisposableSessionServiceB : IDisposableSessionServiceB
    {
        private readonly List<string> _calls;
        public DisposableSessionServiceB(List<string> calls, IDisposableSessionServiceC _) => _calls = calls;
        public Task InitializeAsync(CancellationToken cancellationToken) { _calls.Add("init:B"); return Task.CompletedTask; }
        public Task DisposeAsync(CancellationToken cancellationToken) { _calls.Add("dispose:B"); return Task.CompletedTask; }
    }

    // A depends on B (initialized last)
    private sealed class DisposableSessionServiceA : IDisposableSessionServiceA
    {
        private readonly List<string> _calls;
        public DisposableSessionServiceA(List<string> calls, IDisposableSessionServiceB _) => _calls = calls;
        public Task InitializeAsync(CancellationToken cancellationToken) { _calls.Add("init:A"); return Task.CompletedTask; }
        public Task DisposeAsync(CancellationToken cancellationToken) { _calls.Add("dispose:A"); return Task.CompletedTask; }
    }

    private sealed class DisposableSessionServiceOk : IDisposableSessionServiceOk
    {
        private readonly List<string> _calls;
        public DisposableSessionServiceOk(List<string> calls) => _calls = calls;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisposeAsync(CancellationToken cancellationToken) { _calls.Add("dispose:ok"); return Task.CompletedTask; }
    }

    private sealed class DisposableSessionServiceFailing : IDisposableSessionServiceFailing
    {
        private readonly List<string> _calls;
        public DisposableSessionServiceFailing(List<string> calls) => _calls = calls;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisposeAsync(CancellationToken cancellationToken)
        {
            _calls.Add("dispose:failing");
            throw new InvalidOperationException("disposal failed");
        }
    }

    private sealed class DisposableSceneService : IDisposableSceneService
    {
        private readonly List<string> _calls;
        public DisposableSceneService(List<string> calls) => _calls = calls;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisposeAsync(CancellationToken cancellationToken) { _calls.Add("dispose:scene"); return Task.CompletedTask; }
    }

    private sealed class FailingInitAndDisposeSceneService : IFailingInitAndDisposeSceneService, IDisposable
    {
        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("scene init failed");
        }

        public void Dispose()
        {
            throw new InvalidOperationException("scene dispose failed");
        }
    }
}
