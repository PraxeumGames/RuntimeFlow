using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{
    public sealed class ServiceDisposalLifecycleTests
    {
        [Test]
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
            Assert.That(disposalCalls, Is.EqualTo(new[] { "dispose:A", "dispose:B", "dispose:C" }));
        }

        [Test]
        public async Task BuildAsync_WhenRebuilt_DisposesOwnedGlobalAsyncDisposableServices()
        {
            var service = new AsyncDisposableGlobalService();
            var builder = new GameContextBuilder();
            builder.DefineGlobalScope();
            builder.DefineSessionScope();
            builder.Global().RegisterInstance<IAsyncDisposableGlobalService>(service);
            builder.Session().RegisterInstance<INoopSessionService>(new NoopSessionService());

            await builder.BuildAsync();
            await builder.BuildAsync();

            Assert.That(service.DisposeCount, Is.EqualTo(1));
        }

        [Test]
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

            var ex = await AsyncTestAssert.ThrowsAsync<AggregateException>(() => pipeline.RestartSessionAsync());
            Assert.That(ex, Is.Not.Null);
            Assert.That(ex!.InnerExceptions, Has.Count.EqualTo(1));

            Assert.That(calls, Does.Contain("dispose:failing"));
            Assert.That(calls, Does.Contain("dispose:ok"));
        }

        [Test]
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
            Assert.That(disposalCalls, Is.EqualTo(new[] { "dispose:scene" }));
        }

        [Test]
        public async Task LoadScene_WhenInitializationAndCleanupBothFail_AggregatesBothErrors()
        {
            var failingService = new FailingInitAndDisposeSceneService();
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Scene(new TestSceneScope(s => s.RegisterInstance<IFailingInitAndDisposeSceneService>(failingService)));
            });

            await pipeline.InitializeAsync();

            var exception = await AsyncTestAssert.ThrowsAsync<AggregateException>(() => pipeline.LoadSceneAsync<TestSceneScope>());
            Assert.That(exception, Is.Not.Null);
            Assert.That(
                exception!.InnerExceptions,
                Has.Some.Matches<Exception>(inner =>
                    inner is InvalidOperationException invalid
                    && invalid.Message == "scene init failed"));
            Assert.That(
                exception.InnerExceptions,
                Has.Some.Matches<Exception>(inner =>
                    inner is InvalidOperationException invalid
                    && invalid.Message == "scene dispose failed"));
            Assert.That(pipeline.GetRuntimeStatus().State, Is.EqualTo(RuntimeExecutionState.Failed));
        }

        [Test]
        public async Task InitializeFailure_DisposesPreviouslyInitializedRuntimeFlowServices()
        {
            var initializedService = new InitializedBeforeFailureSessionService();

            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().RegisterInstance<IInitializedBeforeFailureSessionService>(initializedService);
                builder.Session().RegisterInstance<IFailingAfterDependencySessionService>(new FailingAfterDependencySessionService());
            });

            var exception = await AsyncTestAssert.CatchAsync<InvalidOperationException>(() => pipeline.InitializeAsync());

            Assert.That(exception.Message, Is.EqualTo("session init failed after dependency"));
            Assert.That(initializedService.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public async Task InitializeFailure_WhenCallerTokenIsCancelled_DisposesPreviouslyInitializedRuntimeFlowServices()
        {
            using var cancellationSource = new CancellationTokenSource();
            var initializedService = new InitializedBeforeCanceledFailureSessionService();

            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().RegisterInstance<IInitializedBeforeCanceledFailureSessionService>(initializedService);
                builder.Session().RegisterInstance<IFailingAfterCanceledDependencySessionService>(
                    new FailingAfterCanceledDependencySessionService(cancellationSource));
            });

            var exception = await AsyncTestAssert.CatchAsync<InvalidOperationException>(
                () => pipeline.InitializeAsync(cancellationToken: cancellationSource.Token));

            Assert.That(exception.Message, Is.EqualTo("session init failed after canceling caller token"));
            Assert.That(cancellationSource.IsCancellationRequested, Is.True);
            Assert.That(initializedService.DisposeCount, Is.EqualTo(1));
        }

        // --- Service contracts ---
        private interface IDisposableSessionServiceA : ISessionInitializableService, ISessionDisposableService { }
        private interface IDisposableSessionServiceB : ISessionInitializableService, ISessionDisposableService { }
        private interface IDisposableSessionServiceC : ISessionInitializableService, ISessionDisposableService { }
        private interface IAsyncDisposableGlobalService : IGlobalInitializableService, IGlobalDisposableService { }
        private interface INoopSessionService : ISessionInitializableService { }
        private interface IDisposableSessionServiceOk : ISessionInitializableService, ISessionDisposableService { }
        private interface IDisposableSessionServiceFailing : ISessionInitializableService, ISessionDisposableService { }
        private interface IDisposableSceneService : ISceneInitializableService, ISceneDisposableService { }
        private interface IFailingInitAndDisposeSceneService : ISceneInitializableService { }
        private interface IInitializedBeforeFailureSessionService : ISessionInitializableService, ISessionDisposableService { }
        private interface IFailingAfterDependencySessionService : ISessionInitializableService { }
        private interface IInitializedBeforeCanceledFailureSessionService : ISessionInitializableService, ISessionDisposableService { }
        private interface IFailingAfterCanceledDependencySessionService : ISessionInitializableService { }

        // --- Service implementations ---
        // C has no dependencies (initialized first)
        private sealed class DisposableSessionServiceC : IDisposableSessionServiceC
        {
            private readonly List<string> _calls;
            public DisposableSessionServiceC(List<string> calls) => _calls = calls;
            public Task InitializeAsync(CancellationToken cancellationToken) { _calls.Add("init:C"); return Task.CompletedTask; }
            public Task DisposeAsync(CancellationToken cancellationToken) { _calls.Add("dispose:C"); return Task.CompletedTask; }
        }

        private sealed class AsyncDisposableGlobalService : IAsyncDisposableGlobalService
        {
            private int _disposeCount;

            public int DisposeCount => Volatile.Read(ref _disposeCount);

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task DisposeAsync(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _disposeCount);
                return Task.CompletedTask;
            }
        }

        private sealed class NoopSessionService : INoopSessionService
        {
            public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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

        private sealed class InitializedBeforeFailureSessionService : IInitializedBeforeFailureSessionService
        {
            public int DisposeCount { get; private set; }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task DisposeAsync(CancellationToken cancellationToken)
            {
                DisposeCount++;
                return Task.CompletedTask;
            }
        }

        [DependsOn(typeof(IInitializedBeforeFailureSessionService))]
        private sealed class FailingAfterDependencySessionService : IFailingAfterDependencySessionService
        {
            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("session init failed after dependency");
            }
        }

        private sealed class InitializedBeforeCanceledFailureSessionService : IInitializedBeforeCanceledFailureSessionService
        {
            public int DisposeCount { get; private set; }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task DisposeAsync(CancellationToken cancellationToken)
            {
                DisposeCount++;
                return Task.CompletedTask;
            }
        }

        [DependsOn(typeof(IInitializedBeforeCanceledFailureSessionService))]
        private sealed class FailingAfterCanceledDependencySessionService : IFailingAfterCanceledDependencySessionService
        {
            private readonly CancellationTokenSource _cancellationSource;

            public FailingAfterCanceledDependencySessionService(CancellationTokenSource cancellationSource)
            {
                _cancellationSource = cancellationSource;
            }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _cancellationSource.Cancel();
                throw new InvalidOperationException("session init failed after canceling caller token");
            }
        }
    }
}
