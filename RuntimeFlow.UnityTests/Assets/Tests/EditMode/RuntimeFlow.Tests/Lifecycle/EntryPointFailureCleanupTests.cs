using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Tests.Lifecycle
{
    [TestFixture]
    public sealed class EntryPointFailureCleanupTests
    {
        public sealed class CleanupTrace
        {
            public Exception Failure { get; } = new InvalidOperationException("original synchronous entry point failure");
            public TaskCompletionSource<bool> CleanupStarted { get; } = Signal();
            public TaskCompletionSource<bool> ReleaseCleanup { get; } = Signal();
            public TaskCompletionSource<bool> CleanupFinished { get; } = Signal();
            public LocalResource? Local;
            public ParentResource? Parent;
            public int AsyncDisposals;
            public int AsyncInitializations;
            public int UnrelatedConstructions;
            public bool DependencyDisposedDuringCleanup;
        }

        public sealed class LocalResource : IDisposable
        {
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        public sealed class ParentResource : IAsyncInitializable, IDisposable
        {
            public bool Disposed;
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            public void Dispose() => Disposed = true;
        }

        public sealed class AlreadyConstructedGraphService : IAsyncInitializable, IAsyncDisposable
        {
            private readonly CleanupTrace _trace;
            private readonly LocalResource _local;
            private readonly ParentResource _parent;
            public AlreadyConstructedGraphService(CleanupTrace trace, LocalResource local, ParentResource parent)
            {
                _trace = trace;
                _local = local;
                _parent = parent;
                trace.Local = local;
                trace.Parent = parent;
            }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _trace.AsyncInitializations++;
                return Task.CompletedTask;
            }

            public async ValueTask DisposeAsync()
            {
                _trace.CleanupStarted.TrySetResult(true);
                try
                {
                    await _trace.ReleaseCleanup.Task;
                    _trace.DependencyDisposedDuringCleanup = _local.Disposed || _parent.Disposed;
                    _trace.AsyncDisposals++;
                }
                finally { _trace.CleanupFinished.TrySetResult(true); }
            }
        }

        public sealed class UntouchedGraphService : IAsyncInitializable
        {
            private readonly CleanupTrace _trace;
            public UntouchedGraphService(CleanupTrace trace)
            {
                _trace = trace;
                trace.UnrelatedConstructions++;
            }
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _trace.AsyncInitializations++;
                return Task.CompletedTask;
            }
        }

        public sealed class ThrowingSynchronousEntryPoint : IInitializable
        {
            private readonly CleanupTrace _trace;
            private readonly AlreadyConstructedGraphService _service;
            public ThrowingSynchronousEntryPoint(CleanupTrace trace, AlreadyConstructedGraphService service)
            {
                _trace = trace;
                _service = service;
            }
            public void Initialize()
            {
                GC.KeepAlive(_service);
                throw _trace.Failure;
            }
        }

        [TestCase(true, false)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        [TestCase(false, true)]
        [Timeout(10000)]
        public async Task EntryPointFailureAwaitsOnlyAlreadyConstructedGraphServices(bool globalFailure, bool disposeDuringRollback)
        {
            var trace = new CleanupTrace();
            var host = new RuntimeFlowHost(
                builder =>
                {
                    builder.RegisterInstance(trace);
                    builder.Add<ParentResource>();
                    if (globalFailure) InstallFailure(builder);
                },
                builder =>
                {
                    if (!globalFailure) InstallFailure(builder);
                }, TestScope.Options(new CapturingLogger()));
            Task<StartupResult>? startup = null;
            Task? disposal = null;
            try
            {
                startup = host.StartAsync();
                var first = await Task.WhenAny(trace.CleanupStarted.Task, startup, Task.Delay(TimeSpan.FromSeconds(3)));
                Assert.That(first, Is.SameAs(trace.CleanupStarted.Task),
                    "entry-point failure escaped before async cleanup of its already-constructed graph dependency began");
                Assert.That(startup.IsCompleted, Is.False, "the original failure must await rollback");
                Assert.That(trace.Local, Is.Not.Null);
                Assert.That(trace.Parent, Is.Not.Null);
                Assert.That(trace.Local!.Disposed, Is.False);
                Assert.That(trace.Parent!.Disposed, Is.False);
                Assert.That(trace.UnrelatedConstructions, Is.Zero, "rollback must not construct untouched graph services");
                Assert.That(trace.AsyncInitializations, Is.Zero);
                if (disposeDuringRollback)
                {
                    disposal = host.DisposeAsync().AsTask();
                    await Task.Yield();
                    await Task.Yield();
                    Assert.That(disposal.IsCompleted, Is.False, "host teardown must join the reserved pre-graph rollback");
                    Assert.That(trace.Local.Disposed, Is.False);
                    Assert.That(trace.Parent.Disposed, Is.False);
                }
                trace.ReleaseCleanup.TrySetResult(true);
                var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => Within(startup));
                Assert.That(failure.InnerException, Is.SameAs(trace.Failure), "rollback must preserve the original entry-point error");
                await Within(trace.CleanupFinished.Task);
                Assert.That(trace.AsyncDisposals, Is.EqualTo(1));
                Assert.That(trace.DependencyDisposedDuringCleanup, Is.False);
                Assert.That(trace.UnrelatedConstructions, Is.Zero);
                Assert.That(trace.AsyncInitializations, Is.Zero);
                Assert.That(trace.Local.Disposed, Is.True);
                if (!globalFailure && !disposeDuringRollback)
                    Assert.That(trace.Parent.Disposed, Is.False, "a failed session must keep its global parent alive");
            }
            finally
            {
                trace.ReleaseCleanup.TrySetResult(true);
                if (startup != null)
                {
                    try { await Within(startup); }
                    catch (RuntimeFlowException) { }
                }
                if (trace.CleanupStarted.Task.IsCompleted) await Within(trace.CleanupFinished.Task);
                if (disposal != null) await Within(disposal);
                await Within(host.DisposeAsync().AsTask());
            }
        }

        private static void InstallFailure(IContainerBuilder builder)
        {
            builder.Register<LocalResource>(Lifetime.Singleton).AsSelf();
            builder.Add<AlreadyConstructedGraphService>();
            builder.Add<UntouchedGraphService>();
            builder.RegisterEntryPoint<ThrowingSynchronousEntryPoint>();
        }

        public sealed class ThrowingEntryPointConstructor : IInitializable
        {
            public ThrowingEntryPointConstructor(CleanupTrace trace, AlreadyConstructedGraphService service)
            {
                GC.KeepAlive(service);
                throw trace.Failure;
            }
            public void Initialize() => throw new InvalidOperationException("the failed constructor must never publish an entry point");
        }

        [TestCase(true, false)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        [TestCase(false, true)]
        [Timeout(10000)]
        public async Task BuildThrowBeforeResolverReturnAwaitsAlreadyConstructedGraphCleanup(bool globalFailure, bool constructorFailure)
        {
            var trace = new CleanupTrace();
            var host = new RuntimeFlowHost(builder =>
            {
                builder.RegisterInstance(trace);
                builder.Add<ParentResource>();
                if (globalFailure) InstallBuildThrow(builder);
            }, builder =>
            {
                if (!globalFailure) InstallBuildThrow(builder);
            }, TestScope.Options(new CapturingLogger()));
            Task<StartupResult>? startup = null;
            Task? disposal = null;
            try
            {
                startup = host.StartAsync();
                var first = await Task.WhenAny(trace.CleanupStarted.Task, startup, Task.Delay(TimeSpan.FromSeconds(3)));
                Assert.That(first, Is.SameAs(trace.CleanupStarted.Task),
                    "Build/CreateScope threw before returning the resolver and lost its already-cached async graph service");
                Assert.That(startup.IsCompleted, Is.False);
                Assert.That(trace.Local, Is.Not.Null);
                Assert.That(trace.Parent, Is.Not.Null);
                Assert.That(trace.Local!.Disposed, Is.False);
                Assert.That(trace.Parent!.Disposed, Is.False);
                Assert.That(trace.AsyncInitializations, Is.Zero);
                Assert.That(trace.UnrelatedConstructions, Is.Zero);
                disposal = host.DisposeAsync().AsTask();
                await Task.Yield();
                await Task.Yield();
                Assert.That(disposal.IsCompleted, Is.False, "host disposal must wait for the provisional resolver rollback");
                Assert.That(trace.Local.Disposed, Is.False);
                Assert.That(trace.Parent.Disposed, Is.False);
                trace.ReleaseCleanup.TrySetResult(true);
                await ExpectOriginalBuildFailure(startup, trace.Failure);
                await Within(trace.CleanupFinished.Task);
                Assert.That(trace.AsyncDisposals, Is.EqualTo(1));
                Assert.That(trace.DependencyDisposedDuringCleanup, Is.False);
                Assert.That(trace.AsyncInitializations, Is.Zero);
                Assert.That(trace.UnrelatedConstructions, Is.Zero);
                Assert.That(trace.Local.Disposed, Is.True);
            }
            finally
            {
                trace.ReleaseCleanup.TrySetResult(true);
                if (startup != null) await ExpectOriginalBuildFailure(startup, trace.Failure);
                if (trace.CleanupStarted.Task.IsCompleted) await Within(trace.CleanupFinished.Task);
                if (disposal != null) await Within(disposal);
                await Within(host.DisposeAsync().AsTask());
            }

            void InstallBuildThrow(IContainerBuilder builder)
            {
                builder.Register<LocalResource>(Lifetime.Singleton).AsSelf();
                builder.Add<AlreadyConstructedGraphService>();
                builder.Add<UntouchedGraphService>();
                if (constructorFailure) builder.RegisterEntryPoint<ThrowingEntryPointConstructor>();
                else builder.RegisterBuildCallback(resolver =>
                {
                    resolver.Resolve<AlreadyConstructedGraphService>();
                    throw trace.Failure;
                });
            }
        }

        private static async Task ExpectOriginalBuildFailure(Task task, Exception expected)
        {
            try { await Within(task); }
            catch (Exception error)
            {
                var current = error;
                while (current != null)
                {
                    if (ReferenceEquals(current, expected)) return;
                    current = current.InnerException;
                }
                throw;
            }
            Assert.Fail("Build/CreateScope must preserve its original callback/constructor failure");
        }

        public sealed class MetadataTrace
        {
            public Exception Failure { get; } = new InvalidOperationException("original metadata entry point failure");
            public TaskCompletionSource<bool> CleanupStarted { get; } = Signal();
            public TaskCompletionSource<bool> ReleaseCleanup { get; } = Signal();
            public TaskCompletionSource<bool> CleanupFinished { get; } = Signal();
            public bool DependencyDisposed;
            public bool DependencyDisposedDuringCleanup;
        }

        [Init(Phase = "early")]
        public sealed class CachedEarlyDependency : IAsyncInitializable, IAsyncDisposable
        {
            private readonly MetadataTrace _trace;
            public CachedEarlyDependency(MetadataTrace trace) => _trace = trace;
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync()
            {
                _trace.DependencyDisposed = true;
                return default;
            }
        }

        public interface ICachedDependent { }

        public interface IUnknownRollbackDependency { }

        [DependsOn(typeof(IUnknownRollbackDependency))]
        public abstract class CachedDependent : IAsyncInitializable, IAsyncDisposable, ICachedDependent
        {
            private readonly MetadataTrace _trace;
            protected CachedDependent(MetadataTrace trace) => _trace = trace;
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            public async ValueTask DisposeAsync()
            {
                _trace.CleanupStarted.TrySetResult(true);
                try
                {
                    await _trace.ReleaseCleanup.Task;
                    _trace.DependencyDisposedDuringCleanup = _trace.DependencyDisposed;
                }
                finally { _trace.CleanupFinished.TrySetResult(true); }
            }
        }

        [DependsOn(typeof(CachedEarlyDependency))]
        public sealed class CachedExplicitDependent : CachedDependent
        {
            public CachedExplicitDependent(MetadataTrace trace) : base(trace) { }
        }

        [Init(Phase = "late")]
        public sealed class CachedLateDependent : CachedDependent
        {
            public CachedLateDependent(MetadataTrace trace) : base(trace) { }
        }

        public sealed class MetadataThrowingEntryPoint : IInitializable
        {
            private readonly MetadataTrace _trace;
            public MetadataThrowingEntryPoint(MetadataTrace trace, ICachedDependent dependent, CachedEarlyDependency dependency)
            {
                _trace = trace;
                GC.KeepAlive(dependent);
                GC.KeepAlive(dependency);
            }
            public void Initialize() => throw _trace.Failure;
        }

        [TestCase(false)]
        [TestCase(true)]
        [Timeout(10000)]
        public async Task PreGraphCleanupRetainsCachedDeclaredDependencyOrPhaseOrdering(bool phaseDependency)
        {
            var trace = new MetadataTrace();
            var options = TestScope.Options(new CapturingLogger());
            options.Phases = phaseDependency ? new[] { "early", "late" } : Array.Empty<string>();
            var host = new RuntimeFlowHost(builder =>
            {
                builder.RegisterInstance(trace);
                // Both dependencies are constructed by the entry point; neither InitializeAsync runs.
                // The dependent is registered and constructed first, so reverse registration/creation
                // order alone would release the early dependency before its dependent cleanup.
                if (phaseDependency) builder.Add<CachedLateDependent>();
                else builder.Add<CachedExplicitDependent>();
                builder.Add<CachedEarlyDependency>();
                builder.RegisterEntryPoint<MetadataThrowingEntryPoint>();
            }, _ => { }, options);
            Task<StartupResult>? startup = null;
            try
            {
                startup = host.StartAsync();
                var first = await Task.WhenAny(trace.CleanupStarted.Task, startup, Task.Delay(TimeSpan.FromSeconds(3)));
                Assert.That(first, Is.SameAs(trace.CleanupStarted.Task));
                Assert.That(trace.DependencyDisposed, Is.False, "cached semantic prerequisites must outlive dependent cleanup");
                Assert.That(startup.IsCompleted, Is.False);
                trace.ReleaseCleanup.TrySetResult(true);
                var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => Within(startup));
                Assert.That(failure.InnerException, Is.SameAs(trace.Failure));
                await Within(trace.CleanupFinished.Task);
                Assert.That(trace.DependencyDisposedDuringCleanup, Is.False);
                Assert.That(trace.DependencyDisposed, Is.True);
            }
            finally
            {
                trace.ReleaseCleanup.TrySetResult(true);
                if (startup != null)
                {
                    try { await Within(startup); }
                    catch (RuntimeFlowException) { }
                }
                if (trace.CleanupStarted.Task.IsCompleted) await Within(trace.CleanupFinished.Task);
                await Within(host.DisposeAsync().AsTask());
            }
        }

        private static TaskCompletionSource<bool> Signal()
            => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task Within(Task task)
        {
            var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.That(winner, Is.SameAs(task), "entry-point cleanup regression timed out");
            await task;
        }
    }
}
