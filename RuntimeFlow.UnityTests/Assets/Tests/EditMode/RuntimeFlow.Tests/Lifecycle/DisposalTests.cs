using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Lifecycle
{
    /// <summary>Teardown preserves dependencies, uses completion priority for independent services, and contains cleanup failures.</summary>
    [TestFixture]
    public sealed class DisposalTests
    {
        public sealed class Recorder
        {
            public List<string> Disposed { get; } = new List<string>();
        }

        public abstract class Tracked : IAsyncInitializable, IAsyncDisposable
        {
            protected Tracked(Recorder recorder) => Recorder = recorder;

            protected Recorder Recorder { get; }

            public bool FailDispose { get; set; }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;

            public ValueTask DisposeAsync()
            {
                Recorder.Disposed.Add(GetType().Name);
                if (FailDispose) throw new ObjectDisposedException(GetType().Name);
                return new ValueTask();
            }
        }

        public sealed class First : Tracked
        {
            public First(Recorder recorder) : base(recorder) { }
        }

        public sealed class Second : Tracked
        {
            public Second(Recorder recorder, First first) : base(recorder) => First = first;

            public First First { get; }
        }

        public sealed class Third : Tracked
        {
            public Third(Recorder recorder, Second second) : base(recorder) => Second = second;

            public Second Second { get; }
        }

        public sealed class Instanced : Tracked
        {
            public Instanced(Recorder recorder) : base(recorder) { }
        }

        /// <summary>Synchronously disposable only: an owned run releases it once from its verified VContainer tracker.</summary>
        public sealed class SyncOnly : IAsyncInitializable, IDisposable
        {
            private readonly Recorder _recorder;

            public SyncOnly(Recorder recorder) => _recorder = recorder;

            public int Disposals { get; private set; }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;

            public void Dispose()
            {
                Disposals++;
                _recorder.Disposed.Add(nameof(SyncOnly));
            }
        }

        public sealed class NeverRuns : Tracked
        {
            public NeverRuns(Recorder recorder) : base(recorder) { }
        }

        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;
        private Recorder _recorder = null!;

        private readonly RunTracker _tracker = new RunTracker();

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
            _recorder = new Recorder();
        }

        /// <summary>Disposes every run and container this fixture created, so nothing leaks into the next test.</summary>
        [TearDown]
        public void DisposeTrackedRuns() => _tracker.DisposeAll();

        [Test]
        [Timeout(10000)]
        public async Task ServicesAreDisposedInReverseCompletionOrder()
        {
            var instanced = new Instanced(_recorder);
            var container = _tracker.Build(b =>
            {
                b.RegisterInstance(_recorder);
                b.Register<First>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces();
                b.Register<Second>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces();
                b.Register<Third>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces();
                b.RegisterInstance(instanced).AsImplementedInterfaces();
            });

            var run = _tracker.Create(container, "session", _options);
            await run.RunAsync();
            await run.DisposeAsync();

            Assert.That(_recorder.Disposed, Is.EqualTo(new[] { "Instanced", "Third", "Second", "First" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task DisposalContinuesAfterAFailure()
        {
            var container = _tracker.Build(b =>
            {
                b.RegisterInstance(_recorder);
                b.Register<First>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces();
                b.Register<Second>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces();
            });
            container.Resolve<Second>().FailDispose = true;

            var run = _tracker.Create(container, "session", _options);
            await run.RunAsync();
            await AsyncTestAssert.DoesNotThrowAsync(async () => await run.DisposeAsync());

            Assert.That(_recorder.Disposed, Is.EqualTo(new[] { "Second", "First" }));
            Assert.That(_log.Find(LogLevel.Error, "disposing Second"), Is.EqualTo(
                "[RuntimeFlow] session: disposing Second threw ObjectDisposedException; continuing teardown."));
        }

        [Test]
        [Timeout(10000)]
        public async Task ServicesThatNeverRanAreStillDisposed()
        {
            var container = _tracker.Build(b =>
            {
                b.RegisterInstance(_recorder);
                b.Register<NeverRuns>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces();
            });

            var run = _tracker.Create(container, "session", _options);
            await run.DisposeAsync();

            Assert.That(_recorder.Disposed, Is.EqualTo(new[] { "NeverRuns" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task OwnedSynchronousServiceIsReleasedOnceFromTheContainerTracker()
        {
            var global = _tracker.Build(b => b.RegisterInstance(_recorder));
            var session = global.CreateScope(
                b => b.Register<SyncOnly>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces());
            var service = session.Resolve<SyncOnly>();

            var run = _tracker.Create(session, "session", _options, null, ownsScope: true);
            await run.RunAsync();
            Assert.That(service.Disposals, Is.Zero, "teardown has not started yet");

            await run.DisposeAsync();

            Assert.That(service.Disposals, Is.EqualTo(1),
                "an owned run releases each tracked IDisposable once before the residual scope drain");
            Assert.That(_recorder.Disposed, Is.EqualTo(new[] { nameof(SyncOnly) }));

            await run.DisposeAsync();
            Assert.That(service.Disposals, Is.EqualTo(1), "a second teardown disposes nothing again");
        }

        [Test]
        [Timeout(10000)]
        public async Task DisposeAsyncIsIdempotent()
        {
            var container = _tracker.Build(b =>
            {
                b.RegisterInstance(_recorder);
                b.Register<First>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces();
            });

            var run = _tracker.Create(container, "session", _options);
            await run.RunAsync();
            await run.DisposeAsync();
            await run.DisposeAsync();

            Assert.That(_recorder.Disposed, Is.EqualTo(new[] { "First" }));
        }

        [Test]
        [Timeout(10000)]
        public async Task AnOwnedScopeIsDisposedWithTheRun()
        {
            var global = _tracker.Build(b => b.RegisterInstance(_recorder));
            var session = global.CreateScope(b => b.Register<First>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces());

            var run = _tracker.Create(session, "session", _options, null, ownsScope: true);
            await run.RunAsync();
            await run.DisposeAsync();

            Assert.That(_recorder.Disposed, Is.EqualTo(new[] { "First" }));
            Assert.That(run.State, Is.EqualTo(RunState.Disposed));
        }
    }
}
