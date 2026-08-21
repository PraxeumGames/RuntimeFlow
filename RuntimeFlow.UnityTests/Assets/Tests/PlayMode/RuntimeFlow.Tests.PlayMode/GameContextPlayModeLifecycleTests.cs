using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests.PlayMode
{
    /// <summary>
    /// PlayMode coverage for the lifecycle-native container: real Unity main-thread
    /// SynchronizationContext, teardown marshalled from worker threads, and an
    /// end-to-end session restart in the live player loop.
    /// </summary>
    public sealed class GameContextPlayModeLifecycleTests
    {
        private interface IAsyncAffinityServiceA { }
        private interface IAsyncAffinityServiceB { }

        private sealed class DisposalRecorder
        {
            public readonly List<string> Calls = new();
            public int MainThreadId;
            public int TeardownThreadId;
            public bool TeardownHadSynchronizationContext;
        }

        private sealed class MainThreadAffinityService : IAsyncAffinityServiceA, IAsyncDisposableService, IInitializationThreadAffinityProvider
        {
            private readonly DisposalRecorder _recorder;

            public MainThreadAffinityService(DisposalRecorder recorder) => _recorder = recorder;

            public InitializationThreadAffinity ThreadAffinity => InitializationThreadAffinity.MainThread;

            public Task DisposeAsync(CancellationToken cancellationToken)
            {
                _recorder.Calls.Add("dispose:A");
                _recorder.TeardownThreadId = Thread.CurrentThread.ManagedThreadId;
                _recorder.TeardownHadSynchronizationContext = SynchronizationContext.Current != null;
                return Task.CompletedTask;
            }
        }

        private sealed class AnyThreadAffinityService : IAsyncAffinityServiceB, IAsyncDisposableService, IInitializationThreadAffinityProvider
        {
            private readonly DisposalRecorder _recorder;

            public AnyThreadAffinityService(DisposalRecorder recorder) => _recorder = recorder;

            public InitializationThreadAffinity ThreadAffinity => InitializationThreadAffinity.AnyThread;

            public Task DisposeAsync(CancellationToken cancellationToken)
            {
                _recorder.Calls.Add("dispose:B");
                return Task.CompletedTask;
            }
        }

        private interface IAsyncSessionServiceA : ISessionInitializableService, IAsyncDisposableService { }
        private interface IAsyncSessionServiceB : ISessionInitializableService, IAsyncDisposableService { }

        private sealed class AsyncSessionServiceA : IAsyncSessionServiceA
        {
            private readonly List<string> _calls;

            public AsyncSessionServiceA(List<string> calls, IAsyncSessionServiceB _) => _calls = calls;

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _calls.Add("init:A");
                return Task.CompletedTask;
            }

            public Task DisposeAsync(CancellationToken cancellationToken)
            {
                _calls.Add("dispose:A");
                return Task.CompletedTask;
            }
        }

        private sealed class AsyncSessionServiceB : IAsyncSessionServiceB
        {
            private readonly List<string> _calls;

            public AsyncSessionServiceB(List<string> calls) => _calls = calls;

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _calls.Add("init:B");
                return Task.CompletedTask;
            }

            public Task DisposeAsync(CancellationToken cancellationToken)
            {
                _calls.Add("dispose:B");
                return Task.CompletedTask;
            }
        }

        [Test]
        public void IsOnMainThread_IsTrueInPlayMode()
        {
            Assert.That(GameContext.IsOnMainThread(), Is.True, "PlayMode tests run on the Unity main thread.");
            Assert.That(GameContext.MainThreadContext, Is.Not.Null, "The main-thread SynchronizationContext must be captured in PlayMode.");
        }

        [Test]
        public async Task DisposeAsync_FromWorkerThread_MainThreadAffinityTeardown_RunsOnUnityMainThread()
        {
            var recorder = new DisposalRecorder { MainThreadId = Thread.CurrentThread.ManagedThreadId };
            var context = new GameContext
            {
                ExecutionScheduler = InlineInitializationExecutionScheduler.Instance
            };
            context.RegisterInstance(recorder);
            context.Register(typeof(IAsyncAffinityServiceA), typeof(MainThreadAffinityService));
            context.Initialize();

            RecordInitializer(context, typeof(IAsyncAffinityServiceA), typeof(MainThreadAffinityService));
            context.Resolve(typeof(IAsyncAffinityServiceA));

            await Task.Run(() => context.DisposeAsync().AsTask());

            Assert.That(recorder.Calls, Is.EqualTo(new[] { "dispose:A" }));
            Assert.That(
                recorder.TeardownThreadId,
                Is.EqualTo(recorder.MainThreadId),
                "MainThread-affinity teardown must be marshalled back to the Unity main thread.");
            Assert.That(
                recorder.TeardownHadSynchronizationContext,
                Is.True,
                "Teardown on the main thread must run inside the Unity SynchronizationContext.");
        }

        [Test]
        public async Task DisposeAsync_FromWorkerThread_AnyThreadAffinityTeardown_CompletesWithoutDeadlock()
        {
            var recorder = new DisposalRecorder { MainThreadId = Thread.CurrentThread.ManagedThreadId };
            var context = new GameContext
            {
                ExecutionScheduler = InlineInitializationExecutionScheduler.Instance
            };
            context.RegisterInstance(recorder);
            context.Register(typeof(IAsyncAffinityServiceB), typeof(AnyThreadAffinityService));
            context.Initialize();

            RecordInitializer(context, typeof(IAsyncAffinityServiceB), typeof(AnyThreadAffinityService));
            context.Resolve(typeof(IAsyncAffinityServiceB));

            await Task.Run(() => context.DisposeAsync().AsTask());

            Assert.That(recorder.Calls, Is.EqualTo(new[] { "dispose:B" }),
                "AnyThread-affinity teardown must complete when initiated from a worker thread.");
        }

        [Test]
        public async Task Pipeline_RestartSession_DisposesAsyncServicesInReverseOrder_InPlayMode()
        {
            var calls = new List<string>();
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().RegisterInstance<List<string>>(calls);
                builder.Session().Register<IAsyncSessionServiceB, AsyncSessionServiceB>(DiLifetime.Singleton);
                builder.Session().Register<IAsyncSessionServiceA, AsyncSessionServiceA>(DiLifetime.Singleton);
            });

            await pipeline.InitializeAsync();
            calls.Clear();
            await pipeline.RestartSessionAsync();

            Assert.That(calls, Does.Contain("init:A"));
            Assert.That(calls, Does.Contain("init:B"));
            Assert.That(
                calls.FindAll(c => c.StartsWith("dispose:")),
                Is.EqualTo(new[] { "dispose:A", "dispose:B" }),
                "Async session services must be disposed in reverse initialization order by the pipeline in PlayMode.");
        }

        private static void RecordInitializer(GameContext context, Type serviceType, Type implementationType)
        {
            var registrations = context.GetRegistrationsForServiceType(serviceType);
            Assert.That(registrations.Count, Is.EqualTo(1));
            context.RecordInitialized(new ServiceInitializerBinding(
                serviceType,
                implementationType,
                Array.Empty<Type>(),
                registration: registrations[0]));
        }
    }
}