using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Testing;
using UnityEngine.TestTools;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Tests.PlayMode
{
    /// <summary>
    /// What only a running player loop can prove: VContainer entry points tick on frames while the async
    /// graph is still in flight, continuations come back to the Unity main thread, a restart driven from
    /// a coroutine finishes without deadlocking, declared timeouts fire under real frame pacing, and
    /// disposal reaches the services' <see cref="IAsyncDisposable"/>.
    /// </summary>
    [TestFixture]
    public sealed class PlayerLoopTests
    {
        /// <summary>Shared counters; every fixture builds its own, so nothing is static.</summary>
        public sealed class Probe
        {
            public int Starts;
            public int MainThreadId;
            public int ThreadBeforeAwait;
            public int ThreadAfterAwait;
            public bool Disposed;
            public List<bool> Restarts { get; } = new List<bool>();
        }

        /// <summary>A VContainer entry point: it must tick on the first frame after the scope is built.</summary>
        public sealed class CountingStartable : IStartable
        {
            private readonly Probe _probe;

            public CountingStartable(Probe probe) => _probe = probe;

            public void Start() => _probe.Starts++;
        }

        /// <summary>An async service that finishes only when the test releases it.</summary>
        public sealed class GatedService : IAsyncInitializable
        {
            private readonly TaskCompletionSource<bool> _gate =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Release() => _gate.TrySetResult(true);

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                using (cancellationToken.Register(() => _gate.TrySetCanceled()))
                {
                    await _gate.Task;
                }
            }
        }

        /// <summary>Records the managed thread id on both sides of a real await.</summary>
        public sealed class ThreadProbeService : IAsyncInitializable
        {
            private readonly Probe _probe;

            public ThreadProbeService(Probe probe) => _probe = probe;

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _probe.ThreadBeforeAwait = Thread.CurrentThread.ManagedThreadId;
                await Task.Delay(10, cancellationToken);
                _probe.ThreadAfterAwait = Thread.CurrentThread.ManagedThreadId;
            }
        }

        /// <summary>Records whether each run was a restart.</summary>
        public sealed class RestartProbeService : IAsyncInitializable
        {
            private readonly Probe _probe;

            public RestartProbeService(Probe probe) => _probe = probe;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _probe.Restarts.Add(context.IsRestart);
                return Task.CompletedTask;
            }
        }

        /// <summary>Never finishes on its own; its declared timeout has to stop it.</summary>
        [Init(TimeoutSeconds = 0.1)]
        public sealed class HangingService : IAsyncInitializable
        {
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
                => Task.Delay(Timeout.Infinite, cancellationToken);
        }

        /// <summary>Asynchronously disposable service; the host must await its DisposeAsync.</summary>
        public sealed class DisposableService : IAsyncInitializable, IAsyncDisposable
        {
            private readonly Probe _probe;

            public DisposableService(Probe probe) => _probe = probe;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;

            public ValueTask DisposeAsync()
            {
                _probe.Disposed = true;
                return default;
            }
        }

        private Probe _probe = null!;
        private CapturingLogger _log = null!;

        [SetUp]
        public void SetUp()
        {
            _probe = new Probe();
            _log = new CapturingLogger();
        }

        [UnityTest]
        [Timeout(10000)]
        public IEnumerator StartableRunsOnTheFirstPlayerLoopTickAfterBuild()
        {
            var host = new RuntimeFlowHost(
                builder => builder.RegisterInstance(_probe),
                builder =>
                {
                    builder.RegisterEntryPoint<CountingStartable>();
                    builder.Register<GatedService>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces();
                },
                Options());

            var startup = host.StartAsync();

            for (var frame = 0; frame < 30 && _probe.Starts == 0; frame++) yield return null;

            Assert.That(_probe.Starts, Is.EqualTo(1), "the startable ticked on a frame");
            Assert.That(startup.IsCompleted, Is.False, "while the async graph is still gated");

            host.Session.Resolve<GatedService>().Release();

            yield return Await(startup);
            Assert.That(startup.Result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(_probe.Starts, Is.EqualTo(1), "the startable runs once, not once per frame");

            yield return Dispose(host);
        }

        [UnityTest]
        [Timeout(10000)]
        public IEnumerator InitializeAsyncContinuationsResumeOnTheUnityMainThread()
        {
            _probe.MainThreadId = Thread.CurrentThread.ManagedThreadId;
            var host = new RuntimeFlowHost(
                builder => builder.RegisterInstance(_probe),
                builder => builder.Register<ThreadProbeService>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces(),
                Options());

            var startup = host.StartAsync();
            yield return Await(startup);

            Assert.That(_probe.ThreadBeforeAwait, Is.EqualTo(_probe.MainThreadId));
            Assert.That(_probe.ThreadAfterAwait, Is.EqualTo(_probe.MainThreadId),
                "the continuation after Task.Delay came back to the Unity main thread");

            yield return Dispose(host);
        }

        [UnityTest]
        [Timeout(10000)]
        public IEnumerator ARestartDrivenFromACoroutineCompletesWithinFrames()
        {
            var host = new RuntimeFlowHost(
                builder => builder.RegisterInstance(_probe),
                builder => builder.Register<RestartProbeService>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces(),
                Options());

            yield return Await(host.StartAsync());

            var restart = host.RestartAsync("coroutine-restart");
            var frames = 0;
            while (!restart.IsCompleted && frames < 300)
            {
                frames++;
                yield return null;
            }

            Assert.That(restart.IsCompleted, Is.True, "the restart finished without deadlocking the main thread");
            Assert.That(restart.Result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(_probe.Restarts, Is.EqualTo(new[] { false, true }));
            Assert.That(host.RestartCount, Is.EqualTo(1));

            yield return Dispose(host);
        }

        [UnityTest]
        [Timeout(10000)]
        public IEnumerator ADeclaredTimeoutFiresUnderRealFramePacing()
        {
            var options = Options();
            options.TimeoutMultiplier = 1.0;
            var host = new RuntimeFlowHost(
                builder => { },
                builder => builder.Register<HangingService>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces(),
                options);

            var startup = host.StartAsync();
            yield return Settled(startup);

            Assert.That(startup.IsFaulted, Is.True);
            var failure = Unwrap(startup) as RuntimeFlowException;
            Assert.That(failure, Is.Not.Null, "expected a RuntimeFlowException");
            Assert.That(failure!.Service, Is.EqualTo("HangingService"));
            Assert.That(failure.InnerException, Is.TypeOf<TimeoutException>());
            Assert.That(failure.InnerException!.Message, Does.StartWith("HangingService did not complete within 0.1s"));

            yield return Dispose(host);
        }

        [UnityTest]
        [Timeout(10000)]
        public IEnumerator DisposingTheHostDisposesAsyncDisposableServices()
        {
            var host = new RuntimeFlowHost(
                builder => builder.RegisterInstance(_probe),
                builder => builder.Register<DisposableService>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces(),
                Options());

            yield return Await(host.StartAsync());
            Assert.That(_probe.Disposed, Is.False);

            yield return Dispose(host);

            Assert.That(_probe.Disposed, Is.True, "the host awaited DisposeAsync on the service it started");
        }

        private RuntimeFlowOptions Options() => new RuntimeFlowOptions
        {
            Logger = _log,
            TimeoutMultiplier = 0,
            StallWarningAfter = TimeSpan.Zero,
            CancellationGrace = TimeSpan.FromMilliseconds(200)
        };

        /// <summary>Yields frames until the task ends, rethrowing its failure.</summary>
        private static IEnumerator Await(Task task)
        {
            yield return Settled(task);
            if (task.IsFaulted) ExceptionDispatchInfo.Capture(Unwrap(task)!).Throw();
        }

        /// <summary>Yields frames until the task ends, however it ended.</summary>
        private static IEnumerator Settled(Task task)
        {
            while (!task.IsCompleted) yield return null;
        }

        private static IEnumerator Dispose(RuntimeFlowHost host)
        {
            var disposal = host.DisposeAsync().AsTask();
            yield return Settled(disposal);
            if (disposal.IsFaulted) ExceptionDispatchInfo.Capture(Unwrap(disposal)!).Throw();
        }

        private static Exception? Unwrap(Task task)
        {
            var aggregate = task.Exception;
            if (aggregate == null) return null;
            return aggregate.InnerExceptions.Count == 1 ? aggregate.InnerExceptions[0] : aggregate;
        }
    }
}
