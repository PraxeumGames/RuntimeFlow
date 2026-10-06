using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Lifecycle
{
    [TestFixture]
    public sealed class GraphConstructionCleanupTests
    {
        public interface IAbsent { }
        public interface IAliasA : IAsyncInitializable { }
        public interface IAliasB : IAsyncInitializable { }
        public sealed class Trace
        {
            public TaskCompletionSource<bool> CleanupStarted { get; } = Signal();
            public TaskCompletionSource<bool> ReleaseCleanup { get; } = Signal();
            public TaskCompletionSource<bool> CleanupFinished { get; } = Signal();
            public List<string> Order { get; } = new List<string>();
            public Resource? Local;
            public ParentResource? Parent;
            public bool DependencyDisposedDuringCleanup;
            public bool ThrowCleanup;
            public bool StopHostInConstructor;
            public Task? HostDisposal;
            public int Cleanups;
            public int Initializations;
        }

        public sealed class Resource : IDisposable
        {
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        public sealed class ParentResource : IAsyncInitializable, IDisposable
        {
            public bool Disposed;
            public ParentResource(Trace trace) { if (trace.Parent == null) trace.Parent = this; }
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            public void Dispose() => Disposed = true;
        }

        [DependsOn(typeof(IAbsent))]
        public sealed class InvalidService : IAliasA, IAliasB, IAsyncInitializable, IAsyncDisposable
        {
            private readonly Trace _trace;
            private readonly Resource _resource;
            public InvalidService(Trace trace, Resource resource)
            {
                _trace = trace;
                _resource = resource;
                trace.Local = resource;
            }
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _trace.Initializations++;
                return Task.CompletedTask;
            }
            public async ValueTask DisposeAsync()
            {
                _trace.Cleanups++;
                _trace.CleanupStarted.TrySetResult(true);
                try
                {
                    await _trace.ReleaseCleanup.Task;
                    _trace.DependencyDisposedDuringCleanup = _resource.Disposed || (_trace.Parent?.Disposed ?? false);
                    if (_trace.ThrowCleanup) throw new InvalidOperationException("cleanup failure");
                }
                finally { _trace.CleanupFinished.TrySetResult(true); }
            }
        }

        [DependsOn(typeof(IAbsent))]
        public sealed class InvalidChild : IAsyncInitializable, IAsyncDisposable
        {
            private readonly InvalidService _cleanup;
            private readonly ParentResource _parent;
            public InvalidChild(Trace trace, Resource resource, ParentResource parent, RuntimeFlowHost host)
            {
                _cleanup = new InvalidService(trace, resource);
                _parent = parent;
                if (trace.StopHostInConstructor) trace.HostDisposal = host.DisposeAsync().AsTask();
            }
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
                => _cleanup.InitializeAsync(context, cancellationToken);
            public async ValueTask DisposeAsync()
            {
                await _cleanup.DisposeAsync();
                GC.KeepAlive(_parent);
            }
        }

        private static IObjectResolver InvalidScope(Trace trace) => TestScope.Build(builder =>
        {
            builder.RegisterInstance(trace);
            builder.Register<Resource>(Lifetime.Singleton).AsSelf();
            builder.Add<InvalidService>();
        });

        [TestCase(false)]
        [TestCase(true)]
        [Timeout(10000)]
        public async Task OwnedAsyncCreateWaitsForCleanupAndPreservesGraphError(bool throws)
        {
            var trace = new Trace { ThrowCleanup = throws };
            var scope = InvalidScope(trace);
            var log = new CapturingLogger();
            var attempt = ScopeRun.CreateAsync(scope, "invalid", TestScope.Options(log), ownsScope: true);
            try
            {
                await Within(trace.CleanupStarted.Task);
                Assert.That(attempt.IsCompleted, Is.False);
                Assert.That(trace.Local!.Disposed, Is.False);
                trace.ReleaseCleanup.TrySetResult(true);
                var error = await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => Within(attempt));
                Assert.That(error.Message, Does.Contain("IAbsent"));
                Assert.That(trace.DependencyDisposedDuringCleanup, Is.False);
                Assert.That(trace.Local.Disposed, Is.True);
                Assert.That(trace.Cleanups, Is.EqualTo(1));
                if (throws) Assert.That(log.Dump(), Does.Contain("disposing InvalidService threw InvalidOperationException"));
            }
            finally
            {
                trace.ReleaseCleanup.TrySetResult(true);
                await ExpectGraphFailure(attempt);
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task SynchronousOwnedCreateDefersResolverDisposalUntilCleanupFinishes()
        {
            var trace = new Trace();
            var scope = InvalidScope(trace);
            try
            {
                Assert.Throws<InitGraphException>(() => ScopeRun.Create(scope, "invalid", TestScope.Options(new CapturingLogger()), ownsScope: true));
                await Within(trace.CleanupStarted.Task);
                Assert.That(trace.Local!.Disposed, Is.False);
                trace.ReleaseCleanup.TrySetResult(true);
                await Within(trace.CleanupFinished.Task);
                // CleanupFinished is signaled inside the service; allow its awaiting rollback to resume.
                await WaitUntil(() => trace.Local.Disposed);
                Assert.That(trace.DependencyDisposedDuringCleanup, Is.False);
            }
            finally
            {
                trace.ReleaseCleanup.TrySetResult(true);
                await Within(trace.CleanupFinished.Task);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CallerOwnedGraphFailureLeavesServicesAndResolverUntouched(bool asynchronous)
        {
            var trace = new Trace();
            var scope = InvalidScope(trace);
            try
            {
                if (asynchronous) await ExpectGraphFailure(ScopeRun.CreateAsync(scope, "caller", TestScope.Options(new CapturingLogger())));
                else Assert.Throws<InitGraphException>(() => ScopeRun.Create(scope, "caller", TestScope.Options(new CapturingLogger())));
                Assert.That(trace.Cleanups, Is.Zero);
                Assert.That(trace.Local!.Disposed, Is.False);
                Assert.That(scope.Resolve<Resource>(), Is.SameAs(trace.Local));
            }
            finally { scope.Dispose(); }
        }

        public sealed class Dependency : IAsyncInitializable, IAsyncDisposable
        {
            private readonly Trace _trace;
            public Dependency(Trace trace) { _trace = trace; trace.Order.Add("construct-dependency"); }
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync() { _trace.Order.Add("dispose-dependency"); return default; }
        }

        [DependsOn(typeof(IAbsent))]
        public sealed class Dependent : IAsyncInitializable, IAsyncDisposable
        {
            private readonly Trace _trace;
            public Dependent(Trace trace, Dependency dependency) { _trace = trace; trace.Order.Add("construct-dependent"); }
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            public async ValueTask DisposeAsync()
            {
                _trace.Order.Add("dispose-dependent-start");
                _trace.CleanupStarted.TrySetResult(true);
                await _trace.ReleaseCleanup.Task;
                _trace.Order.Add("dispose-dependent-end");
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task CleanupUsesDependenciesWhenRegistrationOrderDiffersFromConstructionOrder()
        {
            var trace = new Trace();
            var scope = TestScope.Build(builder =>
            {
                builder.RegisterInstance(trace);
                builder.Add<Dependent>();
                builder.Add<Dependency>();
            });
            var attempt = ScopeRun.CreateAsync(scope, "dependent-first", TestScope.Options(new CapturingLogger()), ownsScope: true);
            try
            {
                await Within(trace.CleanupStarted.Task);
                Assert.That(trace.Order, Is.EqualTo(new[] { "construct-dependency", "construct-dependent", "dispose-dependent-start" }));
                trace.ReleaseCleanup.TrySetResult(true);
                await ExpectGraphFailure(attempt);
                Assert.That(trace.Order, Is.EqualTo(new[] { "construct-dependency", "construct-dependent", "dispose-dependent-start", "dispose-dependent-end", "dispose-dependency" }));
            }
            finally { trace.ReleaseCleanup.TrySetResult(true); await ExpectGraphFailure(attempt); }
        }

        public sealed class CycleTrace
        {
            public TaskCompletionSource<bool> CleanupStarted { get; } = Signal();
            public TaskCompletionSource<bool> ReleaseCleanup { get; } = Signal();
            public List<string> Order { get; } = new List<string>();
            public CycleDependency? Resource;
            public bool DependencyDisposedDuringCleanup;
        }

        [DependsOn(typeof(CycleB))]
        public sealed class CycleA : IAsyncInitializable, IAsyncDisposable
        {
            private readonly CycleTrace _trace;
            private readonly CycleDependency _resource;
            public CycleA(CycleTrace trace, CycleDependency resource) { _trace = trace; _resource = resource; }
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            public async ValueTask DisposeAsync()
            {
                _trace.Order.Add("A-start");
                _trace.CleanupStarted.TrySetResult(true);
                await _trace.ReleaseCleanup.Task;
                _trace.DependencyDisposedDuringCleanup |= _resource.Disposed;
                _trace.Order.Add("A-end");
            }
        }

        [DependsOn(typeof(CycleA))]
        public sealed class CycleB : IAsyncInitializable, IAsyncDisposable
        {
            private readonly CycleTrace _trace;
            public CycleB(CycleTrace trace) => _trace = trace;
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync()
            {
                _trace.DependencyDisposedDuringCleanup |= _trace.Resource!.Disposed;
                _trace.Order.Add("B");
                return default;
            }
        }

        [DependsOn(typeof(CycleTail))]
        public sealed class CycleDependency : IAsyncInitializable, IAsyncDisposable
        {
            private readonly CycleTrace _trace;
            public bool Disposed;
            public CycleDependency(CycleTrace trace) { _trace = trace; trace.Resource = this; }
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync() { Disposed = true; _trace.Order.Add("C"); return default; }
        }

        public class CycleTail : IAsyncInitializable, IAsyncDisposable
        {
            private readonly CycleTrace _trace;
            public CycleTail(CycleTrace trace) => _trace = trace;
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync() { _trace.Order.Add("D"); return default; }
        }

        [DependsOn(typeof(CycleDependency))]
        public sealed class CyclicTail : CycleTail
        {
            public CyclicTail(CycleTrace trace) : base(trace) { }
        }

        [TestCase(false)]
        [TestCase(true)]
        [Timeout(10000)]
        public async Task InvalidCyclesRetainDependenciesUntilEveryDependentComponentCleanupFinishes(bool secondCycle)
        {
            var trace = new CycleTrace();
            var scope = TestScope.Build(builder =>
            {
                builder.RegisterInstance(trace);
                builder.Add<CycleA>();
                builder.Add<CycleB>();
                builder.Add<CycleDependency>();
                builder.Register<CycleTail>(_ => secondCycle ? new CyclicTail(trace) : new CycleTail(trace), Lifetime.Singleton)
                    .AsSelf().As<IAsyncInitializable>();
            });
            var attempt = ScopeRun.CreateAsync(scope, "cycle-cleanup", TestScope.Options(new CapturingLogger()), ownsScope: true);
            try
            {
                await Within(trace.CleanupStarted.Task);
                Assert.That(trace.Order, Is.EqualTo(new[] { "B", "A-start" }));
                Assert.That(trace.Resource!.Disposed, Is.False,
                    "a dependency outside a cyclic component was released before all component members finished cleanup");
                Assert.That(attempt.IsCompleted, Is.False);
                trace.ReleaseCleanup.TrySetResult(true);
                var error = await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => Within(attempt));
                Assert.That(error.Message, Does.Contain("cycle"));
                Assert.That(trace.DependencyDisposedDuringCleanup, Is.False);
                Assert.That(trace.Order, Is.EqualTo(secondCycle
                    ? new[] { "B", "A-start", "A-end", "D", "C" }
                    : new[] { "B", "A-start", "A-end", "C", "D" }));
                Assert.That(trace.Resource.Disposed, Is.True);
            }
            finally { trace.ReleaseCleanup.TrySetResult(true); await ExpectGraphFailure(attempt); }
        }

        [Test]
        [Timeout(10000)]
        public async Task SharedInstanceIsAsynchronouslyReleasedOnceOnGraphFailure()
        {
            var trace = new Trace();
            var shared = new InvalidService(trace, new Resource());
            var scope = TestScope.Build(builder =>
            {
                builder.Register<IAliasA>(_ => shared, Lifetime.Singleton).As<IAsyncInitializable>();
                builder.Register<IAliasB>(_ => shared, Lifetime.Singleton).As<IAsyncInitializable>();
            });
            var attempt = ScopeRun.CreateAsync(scope, "shared", TestScope.Options(new CapturingLogger()), ownsScope: true);
            try
            {
                await Within(trace.CleanupStarted.Task);
                trace.ReleaseCleanup.TrySetResult(true);
                await ExpectGraphFailure(attempt);
                Assert.That(trace.Cleanups, Is.EqualTo(1));
            }
            finally { trace.ReleaseCleanup.TrySetResult(true); await ExpectGraphFailure(attempt); }
        }

        public sealed class AliasService : IAliasA, IAliasB, IAsyncInitializable, IAsyncDisposable
        {
            private readonly Trace _trace;
            public AliasService(Trace trace) => _trace = trace;
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _trace.Initializations++;
                return Task.CompletedTask;
            }
            public async ValueTask DisposeAsync()
            {
                _trace.Cleanups++;
                _trace.CleanupStarted.TrySetResult(true);
                await _trace.ReleaseCleanup.Task;
            }
            // Deliberately equal objects prove that cleanup deduplicates physical instances.
            public override bool Equals(object? other) => other is AliasService;
            public override int GetHashCode() => 0;
        }

        [TestCase(false, true)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        [TestCase(true, false)]
        [Timeout(10000)]
        public async Task ValidGraphReleasesEachActualInstanceOnce(bool start, bool shared)
        {
            var trace = new Trace();
            var first = new AliasService(trace);
            var second = shared ? first : new AliasService(trace);
            var scope = TestScope.Build(builder =>
            {
                builder.Register<IAliasA>(_ => first, Lifetime.Singleton).As<IAsyncInitializable>();
                builder.Register<IAliasB>(_ => second, Lifetime.Singleton).As<IAsyncInitializable>();
            });
            var run = await ScopeRun.CreateAsync(scope, "valid-alias", TestScope.Options(new CapturingLogger()), ownsScope: true);
            Task? disposal = null;
            try
            {
                if (start) await Within(run.RunAsync());
                disposal = run.DisposeAsync().AsTask();
                await Within(trace.CleanupStarted.Task);
                Assert.That(disposal.IsCompleted, Is.False);
                Assert.That(trace.Cleanups, Is.EqualTo(1));
                trace.ReleaseCleanup.TrySetResult(true);
                await Within(disposal);
                await Within(run.DisposeAsync().AsTask());
                Assert.That(trace.Cleanups, Is.EqualTo(shared ? 1 : 2));
            }
            finally
            {
                trace.ReleaseCleanup.TrySetResult(true);
                if (disposal != null) await Within(disposal);
                await Within(run.DisposeAsync().AsTask());
            }
        }

        public interface IProtectedResourceA : IAsyncInitializable { }
        public interface IProtectedResourceB : IAsyncInitializable { }
        public sealed class DisposalOrderTrace
        {
            public string Mode = "cancelled";
            public bool WithAlias;
            public TaskCompletionSource<bool> DependentStarted { get; } = Signal();
            public TaskCompletionSource<bool> AliasStarted { get; } = Signal();
            public TaskCompletionSource<bool> ReleaseAlias { get; } = Signal();
            public TaskCompletionSource<bool> CleanupStarted { get; } = Signal();
            public TaskCompletionSource<bool> ReleaseCleanup { get; } = Signal();
            public List<string> Order { get; } = new List<string>();
            public ProtectedResource? Resource;
            public bool ResourceDisposedDuringCleanup;
            public int ResourceInitializations;
            public int ResourceDisposals;
        }

        public class ProtectedResource : IProtectedResourceA, IProtectedResourceB, IAsyncDisposable
        {
            private readonly DisposalOrderTrace _trace;
            public bool Disposed;
            public ProtectedResource(DisposalOrderTrace trace) { _trace = trace; trace.Resource = this; }
            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _trace.ResourceInitializations++;
                if (_trace.Mode == "skipped") context.Halt("skip the dependent");
                if (_trace.WithAlias && _trace.ResourceInitializations == 2)
                {
                    _trace.AliasStarted.TrySetResult(true);
                    await _trace.ReleaseAlias.Task;
                }
            }
            public ValueTask DisposeAsync()
            {
                Disposed = true;
                _trace.ResourceDisposals++;
                _trace.Order.Add("resource");
                return default;
            }
        }

        public sealed class ProtectedDependent : IAsyncInitializable, IAsyncDisposable
        {
            private readonly DisposalOrderTrace _trace;
            private readonly IProtectedResourceA _resource;
            public ProtectedDependent(DisposalOrderTrace trace, IProtectedResourceA resource)
            { _trace = trace; _resource = resource; }
            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _trace.DependentStarted.TrySetResult(true);
                if (_trace.Mode == "failed") throw new InvalidOperationException("dependent initialization failed");
                if (_trace.Mode == "cancelled") await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            public async ValueTask DisposeAsync()
            {
                _trace.Order.Add("dependent-start");
                _trace.CleanupStarted.TrySetResult(true);
                await _trace.ReleaseCleanup.Task;
                _trace.ResourceDisposedDuringCleanup = _trace.Resource!.Disposed;
                _trace.Order.Add("dependent-end");
                GC.KeepAlive(_resource);
            }
        }

        [TestCase("cancelled", false)]
        [TestCase("failed", false)]
        [TestCase("unstarted", false)]
        [TestCase("skipped", false)]
        [TestCase("complete", true)]
        [Timeout(10000)]
        public async Task NormalTeardownRetainsDependenciesAcrossCompletionStatesAndAliases(string mode, bool alias)
        {
            var trace = new DisposalOrderTrace { Mode = mode, WithAlias = alias };
            var scope = TestScope.Build(builder =>
            {
                builder.RegisterInstance(trace);
                if (alias)
                {
                    var resource = new ProtectedResource(trace);
                    builder.Register<IProtectedResourceA>(_ => resource, Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
                    builder.Add<ProtectedDependent>();
                    builder.Register<IProtectedResourceB>(_ => resource, Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
                }
                else
                {
                    // Reverse registration order would release the resource first in an unstarted run.
                    builder.Add<ProtectedDependent>();
                    builder.Add<ProtectedResource>();
                }
            });
            var run = await ScopeRun.CreateAsync(scope, "normal-disposal-order", TestScope.Options(new CapturingLogger()), ownsScope: true);
            Task<StartupResult>? startup = null;
            Task? disposal = null;
            try
            {
                if (mode != "unstarted")
                {
                    startup = run.RunAsync();
                    if (mode == "failed")
                        await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => Within(startup));
                    else if (mode == "skipped")
                        Assert.That((await Within(startup)).Outcome, Is.EqualTo(StartupOutcome.Halted));
                    else
                    {
                        await Within(trace.DependentStarted.Task);
                        if (alias)
                        {
                            await Within(trace.AliasStarted.Task);
                            trace.ReleaseAlias.TrySetResult(true);
                            await Within(startup);
                        }
                    }
                }
                disposal = run.DisposeAsync().AsTask();
                await Within(trace.CleanupStarted.Task);
                Assert.That(trace.Resource!.Disposed, Is.False,
                    "completion order or a later completed alias overtook a dependent's asynchronous cleanup");
                Assert.That(trace.ResourceDisposals, Is.Zero);
                Assert.That(disposal.IsCompleted, Is.False);
                trace.ReleaseCleanup.TrySetResult(true);
                await Within(disposal);
                Assert.That(trace.ResourceDisposedDuringCleanup, Is.False);
                Assert.That(trace.ResourceDisposals, Is.EqualTo(1));
                Assert.That(trace.Order, Is.EqualTo(new[] { "dependent-start", "dependent-end", "resource" }));
            }
            finally
            {
                trace.ReleaseAlias.TrySetResult(true);
                trace.ReleaseCleanup.TrySetResult(true);
                if (disposal != null) await Within(disposal);
                await Within(run.DisposeAsync().AsTask());
                if (startup != null)
                {
                    try { await Within(startup); }
                    catch (OperationCanceledException) { }
                    catch (RuntimeFlowException) { }
                }
            }
        }

        [Init(Phase = "early")]
        public sealed class EarlyPhaseResource : ProtectedResource
        {
            public EarlyPhaseResource(DisposalOrderTrace trace) : base(trace) { }
        }

        [Init(Phase = "late")]
        public sealed class LatePhaseDependent : IAsyncInitializable, IAsyncDisposable
        {
            private readonly DisposalOrderTrace _trace;
            public LatePhaseDependent(DisposalOrderTrace trace) => _trace = trace;
            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _trace.DependentStarted.TrySetResult(true);
                if (_trace.Mode == "failed") throw new InvalidOperationException("late initialization failed");
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            public async ValueTask DisposeAsync()
            {
                _trace.Order.Add("dependent-start");
                _trace.CleanupStarted.TrySetResult(true);
                await _trace.ReleaseCleanup.Task;
                _trace.ResourceDisposedDuringCleanup = _trace.Resource!.Disposed;
                _trace.Order.Add("dependent-end");
            }
        }

        [TestCase("failed")]
        [TestCase("cancelled")]
        [Timeout(10000)]
        public async Task PhaseBarrierDependenciesRetainEarlyServicesUntilLateCleanupFinishes(string mode)
        {
            var trace = new DisposalOrderTrace { Mode = mode };
            var scope = TestScope.Build(builder =>
            {
                builder.RegisterInstance(trace);
                builder.Add<EarlyPhaseResource>();
                builder.Add<LatePhaseDependent>();
            });
            var options = TestScope.Options(new CapturingLogger());
            options.Phases = new[] { "early", "late" };
            var run = await ScopeRun.CreateAsync(scope, "phase-disposal-order", options, ownsScope: true);
            var startup = run.RunAsync();
            Task? disposal = null;
            try
            {
                await Within(trace.DependentStarted.Task);
                if (mode == "failed")
                    await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => Within(startup));
                disposal = run.DisposeAsync().AsTask();
                await Within(trace.CleanupStarted.Task);
                Assert.That(trace.Resource!.Disposed, Is.False,
                    "a completed early phase service overtook its failed or cancelled late phase dependent");
                Assert.That(disposal.IsCompleted, Is.False);
                trace.ReleaseCleanup.TrySetResult(true);
                await Within(disposal);
                Assert.That(trace.ResourceDisposedDuringCleanup, Is.False);
                Assert.That(trace.Order, Is.EqualTo(new[] { "dependent-start", "dependent-end", "resource" }));
            }
            finally
            {
                trace.ReleaseCleanup.TrySetResult(true);
                if (disposal != null) await Within(disposal);
                await Within(run.DisposeAsync().AsTask());
                try { await Within(startup); }
                catch (OperationCanceledException) { }
                catch (RuntimeFlowException) { }
            }
        }

        public sealed class IndependentCompleted : IAsyncInitializable, IAsyncDisposable
        {
            private readonly DisposalOrderTrace _trace;
            public IndependentCompleted(DisposalOrderTrace trace) => _trace = trace;
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync() { _trace.Order.Add("independent"); return default; }
        }

        [Test]
        [Timeout(10000)]
        public async Task IndependentCompletedServiceRetainsItsCompletionPriorityDuringDependencyOrderedCleanup()
        {
            var trace = new DisposalOrderTrace();
            var scope = TestScope.Build(builder =>
            {
                builder.RegisterInstance(trace);
                builder.Add<ProtectedResource>();
                builder.Add<ProtectedDependent>();
                builder.Add<IndependentCompleted>();
            });
            var run = await ScopeRun.CreateAsync(scope, "independent-priority", TestScope.Options(new CapturingLogger()), ownsScope: true);
            var startup = run.RunAsync();
            Task? disposal = null;
            try
            {
                await Within(trace.DependentStarted.Task);
                disposal = run.DisposeAsync().AsTask();
                await Within(trace.CleanupStarted.Task);
                Assert.That(trace.Resource!.Disposed, Is.False);
                Assert.That(trace.Order, Is.EqualTo(new[] { "independent", "dependent-start" }));
                trace.ReleaseCleanup.TrySetResult(true);
                await Within(disposal);
                Assert.That(trace.Order, Is.EqualTo(new[] { "independent", "dependent-start", "dependent-end", "resource" }));
            }
            finally
            {
                trace.ReleaseCleanup.TrySetResult(true);
                if (disposal != null) await Within(disposal);
                await Within(run.DisposeAsync().AsTask());
                try { await Within(startup); }
                catch (OperationCanceledException) { }
            }
        }

        [TestCase("host")]
        [TestCase("restart")]
        [TestCase("parent")]
        [TestCase("constructor")]
        [Timeout(10000)]
        public async Task ParentResourcesStayAliveUntilFailedChildConstructionRollbackCompletes(string stop)
        {
            var trace = new Trace { StopHostInConstructor = stop == "constructor" };
            var options = TestScope.Options(new CapturingLogger());
            options.CancellationGrace = TimeSpan.Zero;
            var host = new RuntimeFlowHost(builder => builder.RegisterInstance(trace), builder =>
            {
                if (stop != "parent") builder.Add<ParentResource>();
            }, options);
            Task<ScopeRun>? attempt = null;
            Task? teardown = null;
            try
            {
                await Within(host.StartAsync());
                var parentScope = host.Session;
                ScopeRun? parent = null;
                if (stop == "parent")
                {
                    parentScope = host.Session.CreateScope(builder => builder.Add<ParentResource>());
                    parent = await Within(host.InitializeScopeAsync(parentScope, "managed-parent"));
                }
                var child = parentScope.CreateScope(builder =>
                {
                    builder.Register<Resource>(Lifetime.Singleton).AsSelf();
                    builder.Add<InvalidChild>();
                });
                attempt = host.InitializeScopeAsync(child, "invalid-child");
                await Within(trace.CleanupStarted.Task);
                teardown = stop == "restart" ? host.RestartAsync("during graph rollback")
                    : stop == "parent" ? parent!.DisposeAsync().AsTask()
                    : stop == "constructor" ? trace.HostDisposal!
                    : host.DisposeAsync().AsTask();
                await Task.Yield();
                await Task.Yield();
                Assert.That(attempt.IsCompleted, Is.False, "failed-child awaiter escaped its owned rollback");
                Assert.That(teardown.IsCompleted, Is.False, "ancestor teardown must await pending construction without grace");
                Assert.That(trace.Parent!.Disposed, Is.False);
                Assert.That(trace.Local!.Disposed, Is.False);
                trace.ReleaseCleanup.TrySetResult(true);
                await ExpectGraphFailure(attempt);
                await Within(teardown);
                Assert.That(trace.DependencyDisposedDuringCleanup, Is.False);
                Assert.That(trace.Parent.Disposed, Is.True);
                Assert.That(trace.Local.Disposed, Is.True);
                Assert.That(trace.Initializations, Is.Zero);
            }
            finally
            {
                trace.ReleaseCleanup.TrySetResult(true);
                if (attempt != null) await ExpectGraphFailure(attempt);
                if (teardown != null) await Within(teardown);
                await Within(host.DisposeAsync().AsTask());
            }
        }

        private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private static async Task ExpectGraphFailure(Task task)
        {
            await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => Within(task));
        }
        private static async Task WaitUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Yield();
            Assert.That(condition(), Is.True, "bounded rollback proof timed out");
        }
        private static async Task Within(Task task)
        {
            var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.That(winner, Is.SameAs(task), "bounded rollback proof timed out");
            await task;
        }
        private static async Task<T> Within<T>(Task<T> task) { await Within((Task)task); return await task; }
    }
}
