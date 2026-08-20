using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{
    public sealed class GameContextNativeDisposalTests
    {
        private interface IAsyncServiceA { }
        private interface IAsyncServiceB { }
        private interface IAsyncServiceC { }

        private sealed class DisposalRecorder
        {
            public readonly List<string> Calls = new();
        }

        private sealed class AsyncServiceA : IAsyncServiceA, IAsyncDisposableService
        {
            private readonly DisposalRecorder _recorder;

            public AsyncServiceA(DisposalRecorder recorder) => _recorder = recorder;

            public Task DisposeAsync(CancellationToken cancellationToken)
            {
                _recorder.Calls.Add("dispose:A");
                return Task.CompletedTask;
            }
        }

        private sealed class AsyncServiceB : IAsyncServiceB, IAsyncDisposableService
        {
            private readonly DisposalRecorder _recorder;

            public AsyncServiceB(DisposalRecorder recorder) => _recorder = recorder;

            public Task DisposeAsync(CancellationToken cancellationToken)
            {
                _recorder.Calls.Add("dispose:B");
                return Task.CompletedTask;
            }
        }

        private sealed class AsyncServiceC : IAsyncServiceC, IAsyncDisposableService
        {
            private readonly DisposalRecorder _recorder;

            public AsyncServiceC(DisposalRecorder recorder) => _recorder = recorder;

            public Task DisposeAsync(CancellationToken cancellationToken)
            {
                _recorder.Calls.Add("dispose:C");
                return Task.CompletedTask;
            }
        }

        private sealed class SharedAsyncService : IAsyncServiceA, IAsyncServiceB, IAsyncDisposableService
        {
            private readonly DisposalRecorder _recorder;

            public SharedAsyncService(DisposalRecorder recorder) => _recorder = recorder;

            public Task DisposeAsync(CancellationToken cancellationToken)
            {
                _recorder.Calls.Add("dispose:shared");
                return Task.CompletedTask;
            }
        }

        private sealed class TrackingDisposable : IDisposable
        {
            public int DisposeCount { get; private set; }

            public void Dispose() => DisposeCount++;
        }

        [Test]
        public async Task DisposeAsync_DisposesInitializedServicesInReverseOrder()
        {
            var recorder = new DisposalRecorder();
            var context = new GameContext();
            context.RegisterInstance(recorder);
            context.Register(typeof(IAsyncServiceA), typeof(AsyncServiceA));
            context.Register(typeof(IAsyncServiceB), typeof(AsyncServiceB));
            context.Register(typeof(IAsyncServiceC), typeof(AsyncServiceC));
            context.Initialize();

            RecordInitializer(context, typeof(IAsyncServiceA), typeof(AsyncServiceA));
            RecordInitializer(context, typeof(IAsyncServiceB), typeof(AsyncServiceB));
            RecordInitializer(context, typeof(IAsyncServiceC), typeof(AsyncServiceC));

            context.Resolve(typeof(IAsyncServiceA));
            context.Resolve(typeof(IAsyncServiceB));
            context.Resolve(typeof(IAsyncServiceC));

            await context.DisposeAsync();

            Assert.That(
                recorder.Calls,
                Is.EqualTo(new[] { "dispose:C", "dispose:B", "dispose:A" }),
                "Async services must be disposed in reverse initialization order by the context itself.");
        }

        [Test]
        public async Task DisposeAsync_SameInstanceUnderMultipleServiceTypes_IsDisposedExactlyOnce()
        {
            var recorder = new DisposalRecorder();
            var context = new GameContext();
            context.RegisterInstance(recorder);
            context.Register(typeof(IAsyncServiceA), typeof(SharedAsyncService));
            context.Register(typeof(IAsyncServiceB), typeof(SharedAsyncService));
            context.Initialize();

            RecordInitializer(context, typeof(IAsyncServiceA), typeof(SharedAsyncService));
            RecordInitializer(context, typeof(IAsyncServiceB), typeof(SharedAsyncService));

            context.Resolve(typeof(IAsyncServiceA));
            context.Resolve(typeof(IAsyncServiceB));

            await context.DisposeAsync();

            Assert.That(recorder.Calls, Is.EqualTo(new[] { "dispose:shared" }), "One instance, one async disposal.");
        }

        [Test]
        public void Dispose_Sync_WhenAsyncServicesPresent_ThrowsNotSupportedException()
        {
            var recorder = new DisposalRecorder();
            var context = new GameContext();
            context.RegisterInstance(recorder);
            context.Register(typeof(IAsyncServiceA), typeof(AsyncServiceA));
            context.Initialize();
            RecordInitializer(context, typeof(IAsyncServiceA), typeof(AsyncServiceA));
            context.Resolve(typeof(IAsyncServiceA));

            Assert.Throws<NotSupportedException>(
                () => context.Dispose(),
                "Synchronous disposal must not silently skip async service teardown.");

            Assert.DoesNotThrowAsync(async () => await context.DisposeAsync());
            Assert.That(recorder.Calls, Is.EqualTo(new[] { "dispose:A" }));
        }

        [Test]
        public void Dispose_WithoutAsyncServices_DisposesTrackedInstances()
        {
            var tracked = new TrackingDisposable();
            var context = new GameContext();
            context.RegisterInstance(typeof(TrackingDisposable), tracked);
            context.Initialize();

            context.Dispose();

            Assert.That(tracked.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void Dispose_AfterFailedInitialization_StillDisposesTrackedInstances()
        {
            var tracked = new TrackingDisposable();
            var context = new GameContext();
            context.RegisterInstance(typeof(TrackingDisposable), tracked);
            context.ConfigureContainer(builder =>
                builder.RegisterBuildCallback(_ => throw new InvalidOperationException("build failed")));

            Assert.Throws<InvalidOperationException>(() => context.Initialize());

            Assert.DoesNotThrow(() => context.Dispose());
            Assert.That(
                tracked.DisposeCount,
                Is.EqualTo(1),
                "Owned instances must not leak when initialization fails partway.");
        }

        [Test]
        public async Task DisposeAsync_AfterFailedInitialization_StillDisposesTrackedInstances()
        {
            var tracked = new TrackingDisposable();
            var context = new GameContext();
            context.RegisterInstance(typeof(TrackingDisposable), tracked);
            context.ConfigureContainer(builder =>
                builder.RegisterBuildCallback(_ => throw new InvalidOperationException("build failed")));

            Assert.Throws<InvalidOperationException>(() => context.Initialize());

            await context.DisposeAsync();

            Assert.That(tracked.DisposeCount, Is.EqualTo(1));
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