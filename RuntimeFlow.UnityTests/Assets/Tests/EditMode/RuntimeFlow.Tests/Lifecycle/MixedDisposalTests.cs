using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Internal;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Lifecycle
{
    [TestFixture]
    public sealed class MixedDisposalTests
    {
        public sealed class Trace
        {
            public readonly List<string> Events = new List<string>();
            public bool SyncAlive = true;
            public bool AsyncAlive = true;
            public bool DeadDependency;
            public int Initializations;
            public int SyncDisposals;
            public int AsyncDisposals;
            public bool ThrowSync;
            public int PlainConstructions;
        }

        public sealed class SyncResource : IDisposable
        {
            private readonly Trace _trace;
            public SyncResource(Trace trace) => _trace = trace;
            public void Dispose() { _trace.SyncAlive = false; _trace.Events.Add("sync-resource"); }
        }

        public sealed class AsyncMiddle : IAsyncInitializable, IAsyncDisposable
        {
            private readonly Trace _trace;
            private readonly SyncResource _resource;
            public AsyncMiddle(Trace trace, SyncResource resource) { _trace = trace; _resource = resource; }
            public Task InitializeAsync(InitContext context, CancellationToken token)
            { _trace.Initializations++; return Task.CompletedTask; }
            public ValueTask DisposeAsync()
            {
                _trace.DeadDependency |= !_trace.SyncAlive;
                _trace.AsyncAlive = false;
                _trace.AsyncDisposals++;
                _trace.Events.Add("async-middle");
                GC.KeepAlive(_resource);
                return default;
            }
        }

        public sealed class PlainMiddleDependent : IDisposable
        {
            private readonly Trace _trace;
            private readonly AsyncMiddle _middle;
            public PlainMiddleDependent(Trace trace, AsyncMiddle middle)
            { _trace = trace; _middle = middle; _trace.PlainConstructions++; }
            public void Dispose()
            {
                _trace.DeadDependency |= !_trace.AsyncAlive;
                _trace.SyncDisposals++;
                _trace.Events.Add("sync-dependent");
                GC.KeepAlive(_middle);
            }
        }

        public class AsyncResource : IAsyncInitializable, IAsyncDisposable
        {
            protected readonly Trace Trace;
            public AsyncResource(Trace trace) => Trace = trace;
            public Task InitializeAsync(InitContext context, CancellationToken token)
            { Trace.Initializations++; return Task.CompletedTask; }
            public ValueTask DisposeAsync()
            {
                Trace.AsyncAlive = false;
                Trace.AsyncDisposals++;
                Trace.Events.Add("async-resource");
                return default;
            }
        }

        public class PlainDependent : IDisposable
        {
            protected readonly Trace Trace;
            private readonly AsyncResource _resource;
            public PlainDependent(Trace trace, AsyncResource resource)
            { Trace = trace; _resource = resource; Trace.PlainConstructions++; }
            public void Dispose()
            {
                Trace.DeadDependency |= !Trace.AsyncAlive;
                Trace.SyncDisposals++;
                Trace.Events.Add("sync-dependent");
                GC.KeepAlive(_resource);
                if (Trace.ThrowSync) throw new InvalidOperationException("sync cleanup failed");
            }
        }

        public interface IAliasA : IAsyncInitializable { }
        public interface IAliasB : IAsyncInitializable { }
        public class SyncGraphDependent : PlainDependent, IAsyncInitializable, IAliasA, IAliasB
        {
            public SyncGraphDependent(Trace trace, AsyncResource resource) : base(trace, resource) { }
            public Task InitializeAsync(InitContext context, CancellationToken token)
            { Trace.Initializations++; return Task.CompletedTask; }
        }

        [DependsOn(typeof(Missing))]
        public sealed class InvalidSyncDependent : SyncGraphDependent
        {
            public InvalidSyncDependent(Trace trace, AsyncResource resource) : base(trace, resource) { }
        }
        public sealed class Missing { }

        public sealed class DualDependent : SyncGraphDependent, IAsyncDisposable
        {
            public DualDependent(Trace trace, AsyncResource resource) : base(trace, resource) { }
            public ValueTask DisposeAsync()
            { Trace.DeadDependency |= !Trace.AsyncAlive; Trace.Events.Add("dual-async"); return default; }
        }

        public sealed class PlainDual : IDisposable, IAsyncDisposable
        {
            private readonly Trace _trace;
            public PlainDual(Trace trace, AsyncResource resource) { _trace = trace; GC.KeepAlive(resource); }
            public void Dispose() { _trace.Events.Add("plain-sync"); }
            public ValueTask DisposeAsync() { _trace.Events.Add("plain-async"); return default; }
        }

        [TestCase(false)]
        [TestCase(true)]
        [Timeout(10000)]
        public async Task OwnedSyncDependentReleasesBeforeAsyncResource(bool plain)
        {
            var trace = new Trace();
            var scope = TestScope.Build(b =>
            {
                b.RegisterInstance(trace);
                b.Add<AsyncResource>();
                if (plain) b.Register<PlainDependent>(Lifetime.Singleton).AsSelf();
                else b.Add<SyncGraphDependent>();
            });
            var run = await ScopeRun.CreateAsync(scope, "mixed", TestScope.Options(new CapturingLogger()), ownsScope: true);
            if (plain) scope.Resolve<PlainDependent>();
            await Within(run.RunAsync());
            await Within(run.DisposeAsync().AsTask());
            await Within(run.DisposeAsync().AsTask());
            Assert.That(trace.Events, Is.EqualTo(new[] { "sync-dependent", "async-resource" }));
            Assert.That(trace.DeadDependency, Is.False);
            Assert.That(trace.SyncDisposals, Is.EqualTo(1));
        }

        [Test]
        [Timeout(10000)]
        public async Task OwnedSyncAsyncSyncChainRetainsEachDependency()
        {
            var trace = new Trace();
            var scope = TestScope.Build(b =>
            {
                b.RegisterInstance(trace);
                b.Register<SyncResource>(Lifetime.Singleton).AsSelf();
                b.Add<AsyncMiddle>();
                b.Register<PlainMiddleDependent>(Lifetime.Singleton).AsSelf();
            });
            var run = await ScopeRun.CreateAsync(scope, "three-links", TestScope.Options(new CapturingLogger()), ownsScope: true);
            scope.Resolve<PlainMiddleDependent>();
            await Within(run.DisposeAsync().AsTask());
            Assert.That(trace.Events, Is.EqualTo(new[] { "sync-dependent", "async-middle", "sync-resource" }));
            Assert.That(trace.DeadDependency, Is.False);
        }

        [Test]
        [Timeout(10000)]
        public async Task OwnedDualInterfaceUsesAsyncThenSyncAtItsLifetimeVertex()
        {
            var trace = new Trace();
            var scope = TestScope.Build(b => { b.RegisterInstance(trace); b.Add<AsyncResource>(); b.Add<DualDependent>(); });
            var run = await ScopeRun.CreateAsync(scope, "dual", TestScope.Options(new CapturingLogger()), ownsScope: true);
            await Within(run.DisposeAsync().AsTask());
            Assert.That(trace.Events, Is.EqualTo(new[] { "dual-async", "sync-dependent", "async-resource" }));
            Assert.That(trace.DeadDependency, Is.False);
            Assert.That(trace.SyncDisposals, Is.EqualTo(1));
        }

        [Test]
        [Timeout(10000)]
        public async Task TrackedAliasesDetachEveryEntryAndDisposeOnePhysicalInstance()
        {
            var trace = new Trace { ThrowSync = true };
            var resource = new AsyncResource(trace);
            var shared = new SyncGraphDependent(trace, resource);
            var scope = TestScope.Build(b =>
            {
                b.RegisterInstance(trace);
                b.RegisterInstance(resource).AsSelf().As<IAsyncInitializable>();
                b.Register<IAliasA>(_ => shared, Lifetime.Singleton).As<IAsyncInitializable>();
                b.Register<IAliasB>(_ => shared, Lifetime.Singleton).As<IAsyncInitializable>();
            });
            var run = await ScopeRun.CreateAsync(scope, "aliases", TestScope.Options(new CapturingLogger()), ownsScope: true);
            await Within(run.DisposeAsync().AsTask());
            scope.Dispose();
            Assert.That(trace.SyncDisposals, Is.EqualTo(1));
            Assert.That(trace.AsyncDisposals, Is.EqualTo(1));
            Assert.That(trace.DeadDependency, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        [Timeout(10000)]
        public async Task OwnedGraphFailureUsesMixedOrderAndPreservesGraphError(bool throwSync)
        {
            var trace = new Trace { ThrowSync = throwSync };
            var scope = TestScope.Build(b => { b.RegisterInstance(trace); b.Add<AsyncResource>(); b.Add<InvalidSyncDependent>(); });
            var error = await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => Within(
                ScopeRun.CreateAsync(scope, "invalid-mixed", TestScope.Options(new CapturingLogger()), ownsScope: true)));
            Assert.That(error.Message, Does.Contain(nameof(Missing)));
            Assert.That(trace.Events, Is.EqualTo(new[] { "sync-dependent", "async-resource" }));
            Assert.That(trace.DeadDependency, Is.False);
            Assert.That(trace.SyncDisposals, Is.EqualTo(1));
            Assert.That(trace.Initializations, Is.Zero);
            scope.Dispose();
            Assert.That(trace.SyncDisposals, Is.EqualTo(1));
        }

        [Test]
        [Timeout(10000)]
        public async Task RootContainerIncludesPrivateScopedTracker()
        {
            var trace = new Trace();
            var scope = TestScope.Build(b =>
            {
                b.RegisterInstance(trace); b.Add<AsyncResource>();
                b.Register<PlainDependent>(Lifetime.Scoped).AsSelf();
            });
            var run = await ScopeRun.CreateAsync(scope, "root-scoped", TestScope.Options(new CapturingLogger()), ownsScope: true);
            scope.Resolve<PlainDependent>();
            await Within(run.DisposeAsync().AsTask());
            Assert.That(trace.Events, Is.EqualTo(new[] { "sync-dependent", "async-resource" }));
            Assert.That(trace.DeadDependency, Is.False);
        }

        [Test]
        [Timeout(10000)]
        public async Task ChildRunNeverTakesParentTrackerOwnership()
        {
            var parentTrace = new Trace();
            var childTrace = new Trace();
            var root = TestScope.Build(b => { b.RegisterInstance(parentTrace); b.Register<SyncResource>(Lifetime.Singleton).AsSelf(); });
            var resource = root.Resolve<SyncResource>();
            var child = root.CreateScope(b => { b.RegisterInstance(childTrace); b.Add<AsyncResource>(); b.Add<SyncGraphDependent>(); });
            var run = await ScopeRun.CreateAsync(child, "child", TestScope.Options(new CapturingLogger()), ownsScope: true);
            await Within(run.DisposeAsync().AsTask());
            Assert.That(parentTrace.SyncAlive, Is.True);
            Assert.That(parentTrace.Events, Is.Empty);
            Assert.That(childTrace.Events, Is.EqualTo(new[] { "sync-dependent", "async-resource" }));
            root.Dispose();
            Assert.That(parentTrace.SyncAlive, Is.False);
            GC.KeepAlive(resource);
        }

        [Test]
        [Timeout(10000)]
        public async Task ExistingInstanceAndTransientNeverGainSynchronousOwnership()
        {
            var trace = new Trace();
            var resource = new AsyncResource(trace);
            var instance = new SyncGraphDependent(trace, resource);
            var scope = TestScope.Build(b =>
            {
                b.RegisterInstance(trace); b.RegisterInstance(resource).AsSelf().As<IAsyncInitializable>();
                b.RegisterInstance(instance).As<IAsyncInitializable>();
                b.Register<PlainDependent>(Lifetime.Transient).AsSelf();
            });
            var run = await ScopeRun.CreateAsync(scope, "external-instance", TestScope.Options(new CapturingLogger()), ownsScope: true);
            var transient = scope.Resolve<PlainDependent>();
            await Within(run.DisposeAsync().AsTask());
            Assert.That(trace.SyncDisposals, Is.Zero);
            instance.Dispose(); transient.Dispose();
        }

        [Test]
        [Timeout(10000)]
        public async Task PlainDualInterfaceDoesNotGainFrameworkAsyncOwnership()
        {
            var trace = new Trace();
            var scope = TestScope.Build(b =>
            { b.RegisterInstance(trace); b.Add<AsyncResource>(); b.Register<PlainDual>(Lifetime.Singleton).AsSelf(); });
            var run = await ScopeRun.CreateAsync(scope, "plain-dual", TestScope.Options(new CapturingLogger()), ownsScope: true);
            scope.Resolve<PlainDual>();
            await Within(run.DisposeAsync().AsTask());
            Assert.That(trace.Events, Is.EqualTo(new[] { "plain-sync", "async-resource" }));
        }

        [TestCase(false)]
        [TestCase(true)]
        [Timeout(10000)]
        public async Task BorrowedMixedDependencyIsRejectedBeforeInitialization(bool plainUncreated)
        {
            var trace = new Trace();
            var scope = TestScope.Build(b =>
            {
                b.RegisterInstance(trace); b.Add<AsyncResource>();
                if (plainUncreated) b.Register<PlainDependent>(Lifetime.Singleton).AsSelf();
                else b.Add<SyncGraphDependent>();
            });
            var error = await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => Within(
                ScopeRun.CreateAsync(scope, "borrowed", TestScope.Options(new CapturingLogger()))));
            Assert.That(error.Message, Does.Contain("ownsScope: true"));
            Assert.That(trace.Initializations, Is.Zero);
            Assert.That(trace.AsyncDisposals, Is.Zero);
            Assert.That(trace.SyncDisposals, Is.Zero);
            if (plainUncreated) Assert.That(trace.PlainConstructions, Is.Zero);
            var resource = scope.Resolve<AsyncResource>();
            scope.Dispose();
            await resource.DisposeAsync();
        }

        public sealed class PlainHelper
        {
            public PlainHelper(AsyncResource resource) => Resource = resource;
            public AsyncResource Resource { get; }
        }

        public sealed class HelperDependent : IDisposable
        {
            private readonly Trace _trace;
            private readonly PlainHelper _helper;
            public HelperDependent(Trace trace, PlainHelper helper)
            { _trace = trace; _helper = helper; _trace.PlainConstructions++; }
            public void Dispose()
            {
                _trace.DeadDependency |= !_trace.AsyncAlive;
                _trace.SyncDisposals++;
                _trace.Events.Add("helper-dependent");
                GC.KeepAlive(_helper.Resource);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        [Timeout(10000)]
        public async Task BorrowedSyncDependentThroughPlainHelperIsRejectedBeforeInitialization(bool precached)
        {
            var trace = new Trace();
            var scope = TestScope.Build(b =>
            {
                b.RegisterInstance(trace); b.Add<AsyncResource>();
                b.Register<PlainHelper>(Lifetime.Singleton).AsSelf();
                b.Register<HelperDependent>(Lifetime.Singleton).AsSelf();
            });
            if (precached) scope.Resolve<HelperDependent>();
            var error = await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => Within(
                ScopeRun.CreateAsync(scope, "borrowed-helper", TestScope.Options(new CapturingLogger()))));
            Assert.That(error.Message, Does.Contain(nameof(HelperDependent)));
            Assert.That(trace.Initializations, Is.Zero);
            Assert.That(trace.AsyncDisposals, Is.Zero);
            Assert.That(trace.SyncDisposals, Is.Zero);
            Assert.That(trace.PlainConstructions, Is.EqualTo(precached ? 1 : 0));
            var resource = scope.Resolve<AsyncResource>();
            scope.Dispose();
            await resource.DisposeAsync();
            Assert.That(trace.DeadDependency, Is.False);
        }

        public interface IOpaque { }
        public sealed class LateDependent : PlainDependent, IOpaque
        { public LateDependent(Trace trace, AsyncResource resource) : base(trace, resource) { } }

        [Test]
        [Timeout(10000)]
        public async Task LateOpaqueBorrowedDependentFaultsBeforeReleasingItsResource()
        {
            var trace = new Trace();
            var scope = TestScope.Build(b =>
            {
                b.RegisterInstance(trace); b.Add<AsyncResource>();
                b.Register<IOpaque>(r => new LateDependent(trace, r.Resolve<AsyncResource>()), Lifetime.Singleton);
            });
            var run = await ScopeRun.CreateAsync(scope, "late-borrowed", TestScope.Options(new CapturingLogger()));
            await Within(run.RunAsync());
            scope.Resolve<IOpaque>();
            var error = await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => Within(run.DisposeAsync().AsTask()));
            Assert.That(error.Message, Does.Contain(nameof(LateDependent)));
            Assert.That(trace.AsyncAlive, Is.True);
            Assert.That(trace.SyncDisposals, Is.Zero);
            var resource = scope.Resolve<AsyncResource>();
            scope.Dispose();
            await resource.DisposeAsync();
            Assert.That(trace.Events, Is.EqualTo(new[] { "sync-dependent", "async-resource" }));
            Assert.That(trace.DeadDependency, Is.False);
        }

        [Test]
        [Timeout(10000)]
        public async Task BorrowedAsyncOnlyRunStillReleasesItsGraphService()
        {
            var trace = new Trace();
            var scope = TestScope.Build(b => { b.RegisterInstance(trace); b.Add<AsyncResource>(); });
            var run = await ScopeRun.CreateAsync(scope, "async-only-borrowed", TestScope.Options(new CapturingLogger()));
            await Within(run.DisposeAsync().AsTask());
            Assert.That(trace.AsyncDisposals, Is.EqualTo(1));
            scope.Dispose();
        }

        [Test]
        [Timeout(10000)]
        public async Task BorrowedAsyncDependentMayKeepItsCallerOwnedSyncResource()
        {
            var trace = new Trace();
            var scope = TestScope.Build(b =>
            { b.RegisterInstance(trace); b.Register<SyncResource>(Lifetime.Singleton).AsSelf(); b.Add<AsyncMiddle>(); });
            var run = await ScopeRun.CreateAsync(scope, "safe-borrowed", TestScope.Options(new CapturingLogger()));
            await Within(run.DisposeAsync().AsTask());
            Assert.That(trace.SyncAlive, Is.True);
            Assert.That(trace.Events, Is.EqualTo(new[] { "async-middle" }));
            scope.Dispose();
            Assert.That(trace.DeadDependency, Is.False);
        }

        [Test]
        [Timeout(10000)]
        public async Task HostFromLeavesBorrowedMixedGlobalServicesUnderCallerOwnership()
        {
            var trace = new Trace();
            var scope = TestScope.Build(b => { b.RegisterInstance(trace); b.Add<AsyncResource>(); b.Add<SyncGraphDependent>(); });
            var host = RuntimeFlowHost.From(scope, _ => { }, TestScope.Options(new CapturingLogger()));
            try
            {
                await Within(host.StartAsync());
                await Within(host.DisposeAsync().AsTask());
                Assert.That(trace.Initializations, Is.EqualTo(2));
                Assert.That(trace.SyncDisposals, Is.Zero);
                Assert.That(trace.AsyncDisposals, Is.Zero);
            }
            finally { await Within(host.DisposeAsync().AsTask()); }
            var resource = scope.Resolve<AsyncResource>();
            scope.Dispose();
            await resource.DisposeAsync();
            Assert.That(trace.DeadDependency, Is.False);
        }

        public interface ISharedParent : IAsyncInitializable { }
        public interface IChildSharedAlias : IAsyncInitializable { }
        public interface IParentImplementationGuard { }

        public sealed class ParentRollbackTrace
        {
            public int ParentInitializations;
            public int ParentSyncDisposals;
            public int ParentAsyncDisposals;
            public int ChildSyncDisposals;
            public int ChildAsyncDisposals;
            public bool ParentReleasedDuringChildCleanup;
            public readonly TaskCompletionSource<bool> ChildCleanupStarted = Signal();
            public readonly TaskCompletionSource<bool> ReleaseChildCleanup = Signal();
            public readonly TaskCompletionSource<bool> ChildCleanupFinished = Signal();
            private static TaskCompletionSource<bool> Signal()
                => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public sealed class SharedDisposableParent : ISharedParent, IChildSharedAlias, IParentImplementationGuard,
            IAsyncDisposable, IDisposable
        {
            private readonly ParentRollbackTrace _trace;
            public SharedDisposableParent(ParentRollbackTrace trace) => _trace = trace;
            public Task InitializeAsync(InitContext context, CancellationToken token)
            { _trace.ParentInitializations++; return Task.CompletedTask; }
            public ValueTask DisposeAsync() { _trace.ParentAsyncDisposals++; return default; }
            public void Dispose() => _trace.ParentSyncDisposals++;
        }

        public sealed class ChildRollbackConsumer : IAsyncInitializable, IAsyncDisposable
        {
            private readonly ParentRollbackTrace _trace;
            private readonly ISharedParent _parent;
            public ChildRollbackConsumer(ParentRollbackTrace trace, ISharedParent parent)
            { _trace = trace; _parent = parent; }
            public Task InitializeAsync(InitContext context, CancellationToken token) => Task.CompletedTask;
            public async ValueTask DisposeAsync()
            {
                _trace.ChildAsyncDisposals++;
                _trace.ChildCleanupStarted.TrySetResult(true);
                await _trace.ReleaseChildCleanup.Task;
                _trace.ParentReleasedDuringChildCleanup = _trace.ParentSyncDisposals > 0 || _trace.ParentAsyncDisposals > 0;
                GC.KeepAlive(_parent);
                _trace.ChildCleanupFinished.TrySetResult(true);
            }
        }

        public sealed class ChildRollbackSyncResource : IDisposable
        {
            private readonly ParentRollbackTrace _trace;
            private readonly ChildRollbackConsumer _consumer;
            public ChildRollbackSyncResource(ParentRollbackTrace trace, ChildRollbackConsumer consumer)
            { _trace = trace; _consumer = consumer; }
            public void Dispose() { _trace.ChildSyncDisposals++; GC.KeepAlive(_consumer); }
        }

        [TestCase(false, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, false, true)]
        [TestCase(true, true, true)]
        [TestCase(false, false, false)]
        [Timeout(10000)]
        public async Task OwnedChildGraphRollbackProtectsBorrowedParentDisposalOwnership(bool synchronousCreate, bool childAsyncAlias, bool suppliedParents)
        {
            var trace = new ParentRollbackTrace();
            var shared = new SharedDisposableParent(trace);
            var parent = TestScope.Build(b =>
            {
                b.RegisterInstance(trace);
                b.Register<SharedDisposableParent>(_ => shared, Lifetime.Singleton)
                    .As<ISharedParent>().As<IAsyncInitializable>();
            });
            var options = TestScope.Options(new CapturingLogger());
            var parentRun = await ScopeRun.CreateAsync(parent, "protected-parent", options, ownsScope: true);
            await Within(parentRun.RunAsync());
            var child = parent.CreateScope(b =>
            {
                // This implementation guard makes the inherited parent factory cache its returned
                // physical parent object locally. Both registrations then claim its sync disposal.
                b.Register<SharedDisposableParent>(Lifetime.Singleton).As<IParentImplementationGuard>();
                if (childAsyncAlias)
                    b.Register<IChildSharedAlias>(_ => shared, Lifetime.Singleton).As<IAsyncInitializable>();
                b.Register<ChildRollbackConsumer>(r => new ChildRollbackConsumer(trace, r.Resolve<ISharedParent>()),
                    Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
                b.Register<ChildRollbackSyncResource>(Lifetime.Singleton).AsSelf();
                b.RegisterBuildCallback(r => r.Resolve<ChildRollbackSyncResource>());
            });
            Task<ScopeRun>? attempt = null;
            try
            {
                InitGraphException? immediate = null;
                if (synchronousCreate)
                    immediate = Assert.Throws<InitGraphException>(() => ScopeRun.Create(child, "owned-invalid-child", options,
                        suppliedParents ? new[] { parentRun } : null, ownsScope: true));
                else
                    attempt = ScopeRun.CreateAsync(child, "owned-invalid-child", options, suppliedParents ? new[] { parentRun } : null, ownsScope: true);
                await Within(trace.ChildCleanupStarted.Task);
                Assert.That(trace.ParentSyncDisposals, Is.Zero, "child rollback released a live parent's synchronous ownership");
                Assert.That(trace.ParentAsyncDisposals, Is.Zero, "an alias made child rollback release the parent's async ownership");
                Assert.That(parentRun.State, Is.EqualTo(RunState.Completed));
                Assert.That(trace.ParentInitializations, Is.EqualTo(1));
                if (attempt != null) Assert.That(attempt.IsCompleted, Is.False, "owned async Create escaped gated rollback");
                trace.ReleaseChildCleanup.TrySetResult(true);
                InitGraphException error;
                if (attempt != null)
                    error = await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => Within(attempt));
                else
                {
                    await Within(trace.ChildCleanupFinished.Task);
                    await Until(() => VContainerLifetimeTracker.Capture(child, "sync-rollback-proof").Created.Count == 0);
                    error = immediate!;
                }
                Assert.That(error.Message, Does.Contain("synchronous disposal ownership"));
                Assert.That(trace.ParentReleasedDuringChildCleanup, Is.False);
                Assert.That(trace.ParentSyncDisposals, Is.Zero);
                Assert.That(trace.ParentAsyncDisposals, Is.Zero);
                Assert.That(trace.ChildAsyncDisposals, Is.EqualTo(1));
                Assert.That(trace.ChildSyncDisposals, Is.EqualTo(1));
                child.Dispose();
                Assert.That(trace.ParentSyncDisposals, Is.Zero, "residual child disposal retained a parent tracker alias");
                Assert.That(trace.ChildSyncDisposals, Is.EqualTo(1));
                await Within(parentRun.DisposeAsync().AsTask());
                Assert.That(trace.ParentAsyncDisposals, Is.EqualTo(1));
                Assert.That(trace.ParentSyncDisposals, Is.EqualTo(1));
            }
            finally
            {
                trace.ReleaseChildCleanup.TrySetResult(true);
                if (attempt != null)
                    try { await Within(attempt); } catch (InitGraphException) { }
                await Within(trace.ChildCleanupFinished.Task);
                if (synchronousCreate)
                    await Until(() => VContainerLifetimeTracker.Capture(child, "sync-rollback-proof").Created.Count == 0);
                await Within(parentRun.DisposeAsync().AsTask());
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task OwnedChildRollbackProtectsAncestorFirstCachedDuringGraphDiscovery()
        {
            var trace = new ParentRollbackTrace();
            var shared = new SharedDisposableParent(trace);
            var parent = TestScope.Build(b =>
            {
                b.RegisterInstance(trace);
                b.Register<SharedDisposableParent>(_ => shared, Lifetime.Singleton)
                    .As<ISharedParent>().As<IAsyncInitializable>();
            });
            var options = TestScope.Options(new CapturingLogger());
            var child = parent.CreateScope(b =>
            {
                b.Register<SharedDisposableParent>(Lifetime.Singleton).As<IParentImplementationGuard>();
                b.Register<ChildRollbackConsumer>(r => new ChildRollbackConsumer(trace, r.Resolve<ISharedParent>()),
                    Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
            });
            Assert.That(VContainerLifetimeTracker.Capture(parent, "fresh-parent-proof").Created.Count, Is.Zero,
                "the preconstruction protection snapshot must not already contain the shared parent");
            var attempt = ScopeRun.CreateAsync(child, "fresh-inferred-parent", options, ownsScope: true);
            try
            {
                await Within(trace.ChildCleanupStarted.Task);
                Assert.That(trace.ParentSyncDisposals, Is.Zero);
                Assert.That(trace.ParentAsyncDisposals, Is.Zero);
                trace.ReleaseChildCleanup.TrySetResult(true);
                var error = await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => Within(attempt));
                Assert.That(error.Message, Does.Contain("synchronous disposal ownership"));
                Assert.That(trace.ParentReleasedDuringChildCleanup, Is.False);
                Assert.That(trace.ParentSyncDisposals, Is.Zero,
                    "post-failure refresh must protect the parent object first cached by external-node discovery");
                Assert.That(trace.ParentAsyncDisposals, Is.Zero);
                Assert.That(trace.ChildAsyncDisposals, Is.EqualTo(1));
                child.Dispose();
                Assert.That(trace.ParentSyncDisposals, Is.Zero);
            }
            finally
            {
                trace.ReleaseChildCleanup.TrySetResult(true);
                try { await Within(attempt); } catch (InitGraphException) { }
                var parentRun = await ScopeRun.CreateAsync(parent, "fresh-parent-owner", options, ownsScope: true);
                await Within(parentRun.RunAsync());
                await Within(parentRun.DisposeAsync().AsTask());
            }
            Assert.That(trace.ParentInitializations, Is.EqualTo(1));
            Assert.That(trace.ParentAsyncDisposals, Is.EqualTo(1));
            Assert.That(trace.ParentSyncDisposals, Is.EqualTo(1));
        }

        public sealed class RootScopedSharedResource : IDisposable
        {
            private readonly ParentRollbackTrace _trace;
            public RootScopedSharedResource(ParentRollbackTrace trace) => _trace = trace;
            public void Dispose() => _trace.ParentSyncDisposals++;
        }

        [Test]
        [Timeout(10000)]
        public async Task OwnedChildRollbackProtectsPlainAncestorPrivateRootScopedIdentity()
        {
            var parentTrace = new ParentRollbackTrace();
            var childTrace = new Trace();
            var root = TestScope.Build(b =>
            {
                b.RegisterInstance(parentTrace);
                b.Register<RootScopedSharedResource>(Lifetime.Scoped).AsSelf();
            });
            var rootRun = await ScopeRun.CreateAsync(root, "root-scoped-owner", TestScope.Options(new CapturingLogger()), ownsScope: true);
            var shared = root.Resolve<RootScopedSharedResource>();
            var child = root.CreateScope(b =>
            {
                b.RegisterInstance(childTrace);
                b.Register<RootScopedSharedResource>(_ => shared, Lifetime.Singleton).AsSelf();
                b.Add<AsyncResource>(); b.Add<InvalidSyncDependent>();
                b.RegisterBuildCallback(r => r.Resolve<RootScopedSharedResource>());
            });
            try
            {
                var error = await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => Within(
                    ScopeRun.CreateAsync(child, "invalid-child-scoped-alias", TestScope.Options(new CapturingLogger()), ownsScope: true)));
                Assert.That(error.Message, Does.Contain(nameof(Missing)));
                Assert.That(parentTrace.ParentSyncDisposals, Is.Zero,
                    "a plain ancestor resource in Container.rootScope is protected even without a parent graph service");
                Assert.That(childTrace.SyncDisposals, Is.EqualTo(1));
                Assert.That(childTrace.AsyncDisposals, Is.EqualTo(1));
                child.Dispose();
                Assert.That(parentTrace.ParentSyncDisposals, Is.Zero);
                await Within(rootRun.DisposeAsync().AsTask());
                Assert.That(parentTrace.ParentSyncDisposals, Is.EqualTo(1));
            }
            finally { await Within(rootRun.DisposeAsync().AsTask()); }
        }

        public sealed class ReentryTrace
        {
            public int ParentDisposals;
            public int LocalDisposals;
            public int ResidualDisposals;
            public int AsyncDisposals;
            public int AsyncInitializations;
            public int LocalInitializations;
            public Action DuringAsyncCleanup = () => { };
            public readonly TaskCompletionSource<bool> CleanupStarted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> ReleaseCleanup =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public interface ILateParentAlias { }
        public sealed class LateParentResource : IDisposable, ILateParentAlias
        {
            private readonly ReentryTrace _trace;
            public LateParentResource(ReentryTrace trace) => _trace = trace;
            public void Dispose() => _trace.ParentDisposals++;
        }

        [DependsOn(typeof(Missing))]
        public sealed class InvalidReentryCleanup : IAsyncInitializable, IAsyncDisposable
        {
            private readonly ReentryTrace _trace;
            public InvalidReentryCleanup(ReentryTrace trace) => _trace = trace;
            public Task InitializeAsync(InitContext context, CancellationToken token) => Task.CompletedTask;
            public async ValueTask DisposeAsync()
            {
                _trace.AsyncDisposals++;
                _trace.CleanupStarted.TrySetResult(true);
                await _trace.ReleaseCleanup.Task;
                _trace.DuringAsyncCleanup();
            }
        }

        public sealed class ResidualReentryCleanup : IDisposable
        {
            private readonly ReentryTrace _trace;
            private readonly Action _callback;
            public ResidualReentryCleanup(ReentryTrace trace, Action callback) { _trace = trace; _callback = callback; }
            public void Dispose() { _trace.ResidualDisposals++; _callback(); }
        }

        public interface IAlreadyCleanedAlias { }
        public sealed class AlreadyCleanedLocal : IDisposable, IAlreadyCleanedAlias
        {
            private readonly ReentryTrace _trace;
            public AlreadyCleanedLocal(ReentryTrace trace) => _trace = trace;
            public void Dispose() => _trace.LocalDisposals++;
        }

        [Test]
        [Timeout(10000)]
        public async Task RollbackAsyncCleanupCannotGiveChildOwnershipOfFreshAncestorResource()
        {
            var trace = new ReentryTrace();
            var root = TestScope.Build(b =>
            { b.RegisterInstance(trace); b.Register<LateParentResource>(Lifetime.Singleton).AsSelf(); });
            IScopedObjectResolver? child = null;
            child = root.CreateScope(b =>
            {
                b.Add<InvalidReentryCleanup>();
                b.Register<ILateParentAlias>(_ => root.Resolve<LateParentResource>(), Lifetime.Singleton);
            });
            trace.DuringAsyncCleanup = () =>
            { root.Resolve<LateParentResource>(); child.Resolve<ILateParentAlias>(); };
            var attempt = ScopeRun.CreateAsync(child, "late-parent-during-async", TestScope.Options(new CapturingLogger()), ownsScope: true);
            try
            {
                await Within(trace.CleanupStarted.Task);
                Assert.That(trace.ParentDisposals, Is.Zero);
                trace.ReleaseCleanup.TrySetResult(true);
                var error = await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => Within(attempt));
                Assert.That(error.Message, Does.Contain(nameof(Missing)));
                Assert.That(trace.AsyncDisposals, Is.EqualTo(1));
                Assert.That(trace.ParentDisposals, Is.Zero, "rollback took ownership of an ancestor first created by async cleanup");
                child.Dispose();
                Assert.That(trace.ParentDisposals, Is.Zero);
            }
            finally
            {
                trace.ReleaseCleanup.TrySetResult(true);
                try { await Within(attempt); } catch (InitGraphException) { }
                root.Dispose();
            }
            Assert.That(trace.ParentDisposals, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        [Timeout(10000)]
        public async Task RollbackResidualCallbackCannotGiveChildOwnershipOfAncestorResource(bool parentAlreadyCreated)
        {
            var trace = new ReentryTrace();
            var root = TestScope.Build(b =>
            { b.RegisterInstance(trace); b.Register<LateParentResource>(Lifetime.Singleton).AsSelf(); });
            if (parentAlreadyCreated) root.Resolve<LateParentResource>();
            IScopedObjectResolver? child = null;
            child = root.CreateScope(b =>
            {
                b.Add<InvalidReentryCleanup>();
                b.Register<ILateParentAlias>(_ => root.Resolve<LateParentResource>(), Lifetime.Singleton);
                b.Register<ResidualReentryCleanup>(_ => new ResidualReentryCleanup(trace,
                    () => child!.Resolve<ILateParentAlias>()), Lifetime.Singleton).AsSelf();
            });
            trace.DuringAsyncCleanup = () => child.Resolve<ResidualReentryCleanup>();
            var attempt = ScopeRun.CreateAsync(child, "late-parent-during-residual", TestScope.Options(new CapturingLogger()), ownsScope: true);
            try
            {
                await Within(trace.CleanupStarted.Task);
                Assert.That(trace.ResidualDisposals, Is.Zero);
                trace.ReleaseCleanup.TrySetResult(true);
                var error = await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => Within(attempt));
                Assert.That(error.Message, Does.Contain(nameof(Missing)));
                Assert.That(trace.AsyncDisposals, Is.EqualTo(1));
                Assert.That(trace.ResidualDisposals, Is.EqualTo(1));
                Assert.That(trace.ParentDisposals, Is.Zero, "a residual sync callback inserted the ancestor alias after protection");
                child.Dispose();
                Assert.That(trace.ParentDisposals, Is.Zero);
                Assert.That(trace.ResidualDisposals, Is.EqualTo(1));
            }
            finally
            {
                trace.ReleaseCleanup.TrySetResult(true);
                try { await Within(attempt); } catch (InitGraphException) { }
                root.Dispose();
            }
            Assert.That(trace.ParentDisposals, Is.EqualTo(1));
        }

        [Test]
        [Timeout(10000)]
        public async Task RollbackResidualAliasCannotDisposeAlreadyCleanedLocalInstanceTwice()
        {
            var trace = new ReentryTrace();
            var local = new AlreadyCleanedLocal(trace);
            var root = TestScope.Build(b => b.RegisterInstance(trace));
            IScopedObjectResolver? child = null;
            child = root.CreateScope(b =>
            {
                b.Add<InvalidReentryCleanup>();
                b.Register<AlreadyCleanedLocal>(_ => local, Lifetime.Singleton).AsSelf();
                b.Register<IAlreadyCleanedAlias>(_ => local, Lifetime.Singleton);
                b.Register<ResidualReentryCleanup>(_ => new ResidualReentryCleanup(trace,
                    () => child!.Resolve<IAlreadyCleanedAlias>()), Lifetime.Singleton).AsSelf();
                b.RegisterBuildCallback(r => r.Resolve<AlreadyCleanedLocal>());
            });
            trace.DuringAsyncCleanup = () => child.Resolve<ResidualReentryCleanup>();
            var attempt = ScopeRun.CreateAsync(child, "reentered-local-alias", TestScope.Options(new CapturingLogger()), ownsScope: true);
            try
            {
                await Within(trace.CleanupStarted.Task);
                Assert.That(trace.LocalDisposals, Is.EqualTo(1), "the planned local sync instance was already cleaned before the callback");
                trace.ReleaseCleanup.TrySetResult(true);
                var error = await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => Within(attempt));
                Assert.That(error.Message, Does.Contain(nameof(Missing)));
                Assert.That(trace.AsyncDisposals, Is.EqualTo(1));
                Assert.That(trace.ResidualDisposals, Is.EqualTo(1));
                Assert.That(trace.LocalDisposals, Is.EqualTo(1), "a residual callback re-tracked an already-cleaned physical instance");
                child.Dispose();
                Assert.That(trace.LocalDisposals, Is.EqualTo(1));
            }
            finally
            {
                trace.ReleaseCleanup.TrySetResult(true);
                try { await Within(attempt); } catch (InitGraphException) { }
                root.Dispose();
            }
        }

        public sealed class NormalReentryCleanup : IAsyncInitializable, IAsyncDisposable
        {
            private readonly ReentryTrace _trace;
            public NormalReentryCleanup(ReentryTrace trace) => _trace = trace;
            public Task InitializeAsync(InitContext context, CancellationToken token)
            { _trace.AsyncInitializations++; return Task.CompletedTask; }
            public async ValueTask DisposeAsync()
            {
                _trace.AsyncDisposals++;
                _trace.CleanupStarted.TrySetResult(true);
                await _trace.ReleaseCleanup.Task;
                _trace.DuringAsyncCleanup();
            }
        }

        [DependsOn(typeof(NormalReentryCleanup))]
        public sealed class NormalAlreadyCleanedLocal : IAsyncInitializable, IDisposable, IAlreadyCleanedAlias
        {
            private readonly ReentryTrace _trace;
            public NormalAlreadyCleanedLocal(ReentryTrace trace) => _trace = trace;
            public Task InitializeAsync(InitContext context, CancellationToken token)
            { _trace.LocalInitializations++; return Task.CompletedTask; }
            public void Dispose() => _trace.LocalDisposals++;
        }

        [Test]
        [Timeout(10000)]
        public async Task NormalOwnedResidualAliasCannotDisposeAlreadyCleanedLocalInstanceTwice()
        {
            var trace = new ReentryTrace();
            IObjectResolver? scope = null;
            scope = TestScope.Build(b =>
            {
                b.RegisterInstance(trace); b.Add<NormalReentryCleanup>(); b.Add<NormalAlreadyCleanedLocal>();
                b.Register<IAlreadyCleanedAlias>(r => r.Resolve<NormalAlreadyCleanedLocal>(), Lifetime.Singleton);
                b.Register<ResidualReentryCleanup>(_ => new ResidualReentryCleanup(trace,
                    () => scope!.Resolve<IAlreadyCleanedAlias>()), Lifetime.Singleton).AsSelf();
            });
            trace.DuringAsyncCleanup = () => scope.Resolve<ResidualReentryCleanup>();
            var run = await ScopeRun.CreateAsync(scope, "normal-reentered-local-alias", TestScope.Options(new CapturingLogger()), ownsScope: true);
            await Within(run.RunAsync());
            var disposal = run.DisposeAsync().AsTask();
            try
            {
                await Within(trace.CleanupStarted.Task);
                Assert.That(trace.LocalDisposals, Is.EqualTo(1),
                    "the later-completed local graph service must be cleaned before the async callback can re-add it");
                Assert.That(trace.AsyncInitializations, Is.EqualTo(1));
                Assert.That(trace.LocalInitializations, Is.EqualTo(1));
                trace.ReleaseCleanup.TrySetResult(true);
                await Within(disposal);
                Assert.That(trace.AsyncDisposals, Is.EqualTo(1));
                Assert.That(trace.ResidualDisposals, Is.EqualTo(1));
                Assert.That(trace.LocalDisposals, Is.EqualTo(1));
                await Within(run.DisposeAsync().AsTask());
                scope.Dispose();
                Assert.That(trace.LocalDisposals, Is.EqualTo(1));
                Assert.That(trace.ResidualDisposals, Is.EqualTo(1));
            }
            finally
            {
                trace.ReleaseCleanup.TrySetResult(true);
                await Within(disposal);
            }
        }

        public interface IOptionalHook { }
        public sealed class NullableHookTrace
        {
            public int HookFactories;
            public int OwnerConstructions;
            public int Initializations;
            public int AsyncDisposals;
            public int UntouchedConstructions;
            public bool HookWasNull;
            public NullableHookOwner? Owner;
        }

        public class NullableHookOwner : IAsyncInitializable, IAsyncDisposable
        {
            private readonly NullableHookTrace _trace;
            public NullableHookOwner(NullableHookTrace trace, IOptionalHook? hook)
            {
                _trace = trace;
                trace.Owner = this;
                trace.OwnerConstructions++;
                trace.HookWasNull = hook == null;
            }
            public Task InitializeAsync(InitContext context, CancellationToken token)
            { _trace.Initializations++; return Task.CompletedTask; }
            public ValueTask DisposeAsync() { _trace.AsyncDisposals++; return default; }
        }

        [DependsOn(typeof(Missing))]
        public sealed class InvalidNullableHookOwner : NullableHookOwner
        {
            public InvalidNullableHookOwner(NullableHookTrace trace, IOptionalHook? hook) : base(trace, hook) { }
        }

        public sealed class UntouchedNullableHookService : IAsyncInitializable
        {
            public UntouchedNullableHookService(NullableHookTrace trace) => trace.UntouchedConstructions++;
            public Task InitializeAsync(InitContext context, CancellationToken token) => Task.CompletedTask;
        }

        private static void InstallNullableHook(IContainerBuilder builder, NullableHookTrace trace)
        {
            builder.RegisterInstance(trace);
            builder.Register<IOptionalHook>(_ => { trace.HookFactories++; return null!; }, Lifetime.Singleton);
        }

        [Test]
        [Timeout(10000)]
        public async Task NullPlainCachedFactoryDoesNotAbortNormalOwnedAsyncCleanup()
        {
            var trace = new NullableHookTrace();
            var scope = TestScope.Build(b => { InstallNullableHook(b, trace); b.Add<NullableHookOwner>(); });
            var run = await ScopeRun.CreateAsync(scope, "null-hook-normal", TestScope.Options(new CapturingLogger()), ownsScope: true);
            try
            {
                await Within(run.RunAsync());
                Assert.That(trace.HookWasNull, Is.True, "nullable constructor injection accepted the factory's null result");
                await Within(run.DisposeAsync().AsTask());
                Assert.That(trace.AsyncDisposals, Is.EqualTo(1));
                Assert.That(trace.HookFactories, Is.EqualTo(1));
                Assert.That(trace.OwnerConstructions, Is.EqualTo(1));
                Assert.That(trace.Initializations, Is.EqualTo(1));
                await Within(run.DisposeAsync().AsTask());
                Assert.That(trace.AsyncDisposals, Is.EqualTo(1));
            }
            finally
            {
                await Within(run.DisposeAsync().AsTask());
                if (trace.AsyncDisposals == 0 && trace.Owner != null) await trace.Owner.DisposeAsync();
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task NullPlainCachedFactoryDoesNotAbortOwnedGraphRollbackOrReplaceItsError()
        {
            var trace = new NullableHookTrace();
            var scope = TestScope.Build(b => { InstallNullableHook(b, trace); b.Add<InvalidNullableHookOwner>(); });
            try
            {
                var error = await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => Within(
                    ScopeRun.CreateAsync(scope, "null-hook-invalid", TestScope.Options(new CapturingLogger()), ownsScope: true)));
                Assert.That(error.Message, Does.Contain(nameof(Missing)));
                Assert.That(trace.HookWasNull, Is.True);
                Assert.That(trace.AsyncDisposals, Is.EqualTo(1));
                Assert.That(trace.HookFactories, Is.EqualTo(1));
                Assert.That(trace.OwnerConstructions, Is.EqualTo(1));
                Assert.That(trace.Initializations, Is.Zero);
            }
            finally
            {
                scope.Dispose();
                if (trace.AsyncDisposals == 0 && trace.Owner != null) await trace.Owner.DisposeAsync();
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task NullPlainCachedFactoryDoesNotAbortAlreadyCreatedGraphRecovery()
        {
            var trace = new NullableHookTrace();
            var scope = TestScope.Build(b =>
            { InstallNullableHook(b, trace); b.Add<NullableHookOwner>(); b.Add<UntouchedNullableHookService>(); });
            scope.Resolve<NullableHookOwner>();
            try
            {
                await Within(ScopeRun.ReleaseCreatedGraphAsync(scope, "null-hook-pregraph", TestScope.Options(new CapturingLogger())));
                Assert.That(trace.HookWasNull, Is.True);
                Assert.That(trace.AsyncDisposals, Is.EqualTo(1));
                Assert.That(trace.HookFactories, Is.EqualTo(1));
                Assert.That(trace.OwnerConstructions, Is.EqualTo(1));
                Assert.That(trace.Initializations, Is.Zero);
                Assert.That(trace.UntouchedConstructions, Is.Zero, "snapshot recovery must not construct an untouched graph service");
            }
            finally
            {
                scope.Dispose();
                if (trace.AsyncDisposals == 0 && trace.Owner != null) await trace.Owner.DisposeAsync();
            }
        }

        public sealed class EagerCacheTrace
        {
            public int OwnerFactories;
            public int OwnerConstructions;
            public int DependencyConstructions;
            public int AsyncInitializations;
            public int AsyncDisposals;
            public int UntouchedConstructions;
            public bool DependencyReleasedDuringCleanup;
            public EagerCachedGraphOwner? Owner;
            public EagerHeldDependency? Dependency;
            public readonly TaskCompletionSource<bool> CleanupStarted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> ReleaseCleanup =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> CleanupFinished =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public sealed class EagerHeldDependency : IDisposable
        {
            public bool Disposed;
            public EagerHeldDependency(EagerCacheTrace trace)
            { trace.DependencyConstructions++; trace.Dependency = this; }
            public void Dispose() => Disposed = true;
        }

        public sealed class EagerCachedGraphOwner : IAsyncInitializable, IAsyncDisposable
        {
            private readonly EagerCacheTrace _trace;
            private readonly EagerHeldDependency _dependency;
            public EagerCachedGraphOwner(EagerCacheTrace trace, EagerHeldDependency dependency)
            { _trace = trace; _dependency = dependency; trace.Owner = this; trace.OwnerConstructions++; }
            public Task InitializeAsync(InitContext context, CancellationToken token)
            { _trace.AsyncInitializations++; return Task.CompletedTask; }
            public async ValueTask DisposeAsync()
            {
                _trace.AsyncDisposals++;
                _trace.CleanupStarted.TrySetResult(true);
                await _trace.ReleaseCleanup.Task;
                _trace.DependencyReleasedDuringCleanup = _dependency.Disposed;
                _trace.CleanupFinished.TrySetResult(true);
            }
        }

        public sealed class EagerUntouchedGraphService : IAsyncInitializable
        {
            public EagerUntouchedGraphService(EagerCacheTrace trace) => trace.UntouchedConstructions++;
            public Task InitializeAsync(InitContext context, CancellationToken token) => Task.CompletedTask;
        }

        [TestCase(false)]
        [TestCase(true)]
        [Timeout(10000)]
        public async Task OwnedEarlyPreflightFailureRecoversEagerCachedGraphOwnership(bool synchronousCreate)
        {
            var trace = new EagerCacheTrace();
            var scope = TestScope.Build(b =>
            {
                b.RegisterInstance(trace);
                b.Register<EagerHeldDependency>(Lifetime.Singleton).AsSelf();
                b.Register<EagerCachedGraphOwner>(r =>
                { trace.OwnerFactories++; return new EagerCachedGraphOwner(trace, r.Resolve<EagerHeldDependency>()); },
                    Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
                b.Add<EagerUntouchedGraphService>();
                b.RegisterBuildCallback(r => r.Resolve<EagerCachedGraphOwner>());
            });
            var options = TestScope.Options(new CapturingLogger());
            options.TimeoutMultiplier = double.NaN;
            Task<ScopeRun>? attempt = null;
            try
            {
                InitGraphException? immediate = null;
                if (synchronousCreate)
                    immediate = Assert.Throws<InitGraphException>(() => ScopeRun.Create(scope, "early-preflight", options, ownsScope: true));
                else attempt = ScopeRun.CreateAsync(scope, "early-preflight", options, ownsScope: true);
                if (synchronousCreate)
                    Assert.That(trace.CleanupStarted.Task.IsCompleted, Is.True,
                        "owned sync Create must begin cleanup of eager cached graph services before returning its error");
                else
                {
                    var first = await Task.WhenAny(trace.CleanupStarted.Task, attempt!, Task.Delay(TimeSpan.FromSeconds(3)));
                    Assert.That(first, Is.SameAs(trace.CleanupStarted.Task),
                        "early graph validation escaped before recovering eager cached async ownership");
                    Assert.That(attempt!.IsCompleted, Is.False, "owned async Create must await complete rollback");
                }
                Assert.That(trace.Dependency!.Disposed, Is.False, "the eager owner's sync dependency must stay alive during gated cleanup");
                Assert.That(trace.AsyncDisposals, Is.EqualTo(1));
                Assert.That(trace.AsyncInitializations, Is.Zero);
                Assert.That(trace.OwnerFactories, Is.EqualTo(1));
                Assert.That(trace.OwnerConstructions, Is.EqualTo(1));
                Assert.That(trace.DependencyConstructions, Is.EqualTo(1));
                Assert.That(trace.UntouchedConstructions, Is.Zero);
                trace.ReleaseCleanup.TrySetResult(true);
                InitGraphException error;
                if (attempt != null)
                    error = await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => Within(attempt));
                else
                {
                    await Until(() => VContainerLifetimeTracker.Capture(scope, "early-preflight-sync-proof").Created.Count == 0);
                    error = immediate!;
                }
                Assert.That(error.Message, Does.Contain("TimeoutMultiplier"), "rollback must preserve the original option failure");
                Assert.That(trace.DependencyReleasedDuringCleanup, Is.False);
                Assert.That(trace.Dependency.Disposed, Is.True);
                Assert.That(trace.AsyncDisposals, Is.EqualTo(1));
                Assert.That(trace.OwnerFactories, Is.EqualTo(1));
                Assert.That(trace.OwnerConstructions, Is.EqualTo(1));
                Assert.That(trace.UntouchedConstructions, Is.Zero);
            }
            finally
            {
                trace.ReleaseCleanup.TrySetResult(true);
                if (attempt != null)
                    try { await Within(attempt); } catch (InitGraphException) { }
                if (trace.AsyncDisposals == 0 && trace.Owner != null) await trace.Owner.DisposeAsync();
                if (trace.CleanupStarted.Task.IsCompleted) await Within(trace.CleanupFinished.Task);
                scope.Dispose();
            }
        }

        private static async Task Until(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Yield();
            Assert.That(condition(), Is.True, "bounded rollback completion proof timed out");
        }

        private static async Task Within(Task task)
        {
            var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.That(winner, Is.SameAs(task), "bounded mixed teardown timed out");
            await task;
        }
    }
}
