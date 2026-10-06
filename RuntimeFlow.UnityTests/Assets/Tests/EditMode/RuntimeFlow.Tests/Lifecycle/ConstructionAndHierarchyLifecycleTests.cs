using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Lifecycle
{
    [TestFixture]
    public sealed class ConstructionAndHierarchyLifecycleTests
    {
        public sealed class ConstructionTrace
        {
            public bool Quit;
            public int Initializations;
            public int Disposals;
            public Task? Disposal;
        }

        public sealed class AbortsHostInConstructor : IAsyncInitializable, IAsyncDisposable
        {
            private readonly ConstructionTrace _trace;

            public AbortsHostInConstructor(ConstructionTrace trace, RuntimeFlowHost host)
            {
                _trace = trace;
                if (trace.Quit) host.OnQuitting();
                else trace.Disposal = host.DisposeAsync().AsTask();
            }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _trace.Initializations++;
                return Task.CompletedTask;
            }

            public async ValueTask DisposeAsync()
            {
                await Task.Yield();
                _trace.Disposals++;
            }
        }

        [TestCase(true, false)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        [TestCase(false, true)]
        [Timeout(10000)]
        public async Task ConstructorAbortMustPreventNewGlobalOrSessionInitializers(bool global, bool quit)
        {
            var trace = new ConstructionTrace { Quit = quit };
            var log = new CapturingLogger();
            var host = new RuntimeFlowHost(
                builder =>
                {
                    builder.RegisterInstance(trace);
                    if (global) builder.Add<AbortsHostInConstructor>();
                },
                builder =>
                {
                    if (!global) builder.Add<AbortsHostInConstructor>();
                }, TestScope.Options(log));
            try
            {
                Exception? abort = null;
                try { await Within(host.StartAsync()); }
                catch (ObjectDisposedException error) { abort = error; }
                catch (OperationCanceledException error) { abort = error; }
                Assert.That(abort, quit ? Is.InstanceOf<OperationCanceledException>() : Is.InstanceOf<ObjectDisposedException>());
                Assert.That(trace.Initializations, Is.Zero,
                    "a newly constructed run started after its constructor disposed or quit the host\n" + log.Dump());
                Assert.That(trace.Disposals, Is.EqualTo(1), "the abort awaiter must observe completed cleanup of constructed services");
                Assert.That(host.State, Is.EqualTo(quit ? RunState.Cancelled : RunState.Disposed));
            }
            finally
            {
                if (trace.Disposal != null) await Within(trace.Disposal);
                await Within(host.DisposeAsync().AsTask());
            }
        }

        public sealed class HierarchyTrace
        {
            public TaskCompletionSource<bool> CleanupStarted { get; } = Signal();
            public TaskCompletionSource<bool> ReleaseCleanup { get; } = Signal();
            public bool ParentDisposed;
            public bool ParentDisposedDuringCleanup;
            public ScopeRun? ReentrantParent;
            public Task? ReentrantDisposal;
        }

        public sealed class ChildParentResource : IAsyncInitializable, IAsyncDisposable
        {
            private readonly HierarchyTrace _trace;
            public ChildParentResource(HierarchyTrace trace) => _trace = trace;
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync()
            {
                _trace.ParentDisposed = true;
                return default;
            }
        }

        public sealed class GrandchildCleanup : IAsyncInitializable, IAsyncDisposable
        {
            private readonly HierarchyTrace _trace;
            private readonly ChildParentResource _parent;
            public GrandchildCleanup(HierarchyTrace trace, ChildParentResource parent)
            {
                _trace = trace;
                _parent = parent;
            }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            public async ValueTask DisposeAsync()
            {
                // Retain the actual injected parent throughout asynchronous cleanup.
                GC.KeepAlive(_parent);
                _trace.CleanupStarted.TrySetResult(true);
                if (_trace.ReentrantParent != null)
                    _trace.ReentrantDisposal = _trace.ReentrantParent.DisposeAsync().AsTask();
                await _trace.ReleaseCleanup.Task;
                _trace.ParentDisposedDuringCleanup = _trace.ParentDisposed;
                GC.KeepAlive(_parent);
            }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [Timeout(10000)]
        public async Task DirectManagedParentDisposalMustJoinItsDisposingGrandchild(bool unmanagedAncestor, bool reentrant)
        {
            var trace = new HierarchyTrace();
            var host = new RuntimeFlowHost(builder => builder.RegisterInstance(trace), _ => { },
                TestScope.Options(new CapturingLogger()));
            Task? grandchildDisposal = null;
            Task? parentDisposal = null;
            IScopedObjectResolver? unmanaged = null;
            try
            {
                await Within(host.StartAsync());
                var parentScope = host.Session.CreateScope(builder => builder.Add<ChildParentResource>());
                var parent = await Within(host.InitializeScopeAsync(parentScope, "parent-child"));
                if (reentrant) trace.ReentrantParent = parent;
                if (unmanagedAncestor) unmanaged = parentScope.CreateScope(_ => { });
                var grandchildScope = (unmanaged ?? parentScope).CreateScope(builder => builder.Add<GrandchildCleanup>());
                var grandchild = await Within(host.InitializeScopeAsync(grandchildScope, "grandchild"));
                grandchildDisposal = grandchild.DisposeAsync().AsTask();
                await Within(trace.CleanupStarted.Task);
                parentDisposal = parent.DisposeAsync().AsTask();
                if (reentrant) Assert.That(parentDisposal, Is.SameAs(trace.ReentrantDisposal));
                await Task.Yield();
                await Task.Yield();
                Assert.That(trace.ParentDisposed, Is.False,
                    "direct parent ScopeRun disposal overtook asynchronous cleanup of its initialized grandchild");
                Assert.That(parentDisposal.IsCompleted, Is.False,
                    "the parent teardown must retain and join the pending descendant teardown");
            }
            finally
            {
                trace.ReleaseCleanup.TrySetResult(true);
                if (grandchildDisposal != null) await Within(grandchildDisposal);
                if (parentDisposal != null) await Within(parentDisposal);
                unmanaged?.Dispose();
                await Within(host.DisposeAsync().AsTask());
            }
            Assert.That(trace.ParentDisposedDuringCleanup, Is.False);
        }

        public sealed class RunningTrace
        {
            public TaskCompletionSource<bool> Started { get; } = Signal();
            public TaskCompletionSource<bool> CleanupStarted { get; } = Signal();
            public TaskCompletionSource<bool> ReleaseCleanup { get; } = Signal();
            public bool Cancelled;
        }

        public sealed class RunningGrandchild : IAsyncInitializable, IAsyncDisposable
        {
            private readonly RunningTrace _trace;
            private readonly HierarchyTrace _hierarchy;
            private readonly ChildParentResource _parent;
            public RunningGrandchild(RunningTrace trace, HierarchyTrace hierarchy, ChildParentResource parent)
            {
                _trace = trace;
                _hierarchy = hierarchy;
                _parent = parent;
            }

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _trace.Started.TrySetResult(true);
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException)
                {
                    _trace.Cancelled = true;
                    throw;
                }
            }

            public async ValueTask DisposeAsync()
            {
                _trace.CleanupStarted.TrySetResult(true);
                await _trace.ReleaseCleanup.Task;
                _hierarchy.ParentDisposedDuringCleanup = _hierarchy.ParentDisposed;
                GC.KeepAlive(_parent);
            }
        }

        public sealed class RunningSibling : IAsyncInitializable
        {
            private readonly RunningTrace _trace;
            public RunningSibling(RunningTrace trace) => _trace = trace;
            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _trace.Started.TrySetResult(true);
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException)
                {
                    _trace.Cancelled = true;
                    throw;
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        [Timeout(10000)]
        public async Task DirectParentDisposalCancelsLiveDescendantsAndPreservesUnrelatedSibling(bool unmanagedAncestor)
        {
            var hierarchy = new HierarchyTrace();
            var descendantTrace = new RunningTrace();
            var siblingTrace = new RunningTrace();
            var host = new RuntimeFlowHost(builder => builder.RegisterInstance(hierarchy), _ => { },
                TestScope.Options(new CapturingLogger()));
            Task<ScopeRun>? descendantStartup = null;
            Task<ScopeRun>? siblingStartup = null;
            Task? parentDisposal = null;
            IScopedObjectResolver? unmanaged = null;
            try
            {
                await Within(host.StartAsync());
                var parentScope = host.Session.CreateScope(builder => builder.Add<ChildParentResource>());
                var parent = await Within(host.InitializeScopeAsync(parentScope, "parent-child"));
                if (unmanagedAncestor) unmanaged = parentScope.CreateScope(_ => { });
                var descendantScope = (unmanaged ?? parentScope).CreateScope(builder =>
                {
                    builder.RegisterInstance(descendantTrace);
                    builder.Add<RunningGrandchild>();
                });
                descendantStartup = host.InitializeScopeAsync(descendantScope, "running-grandchild");
                var siblingScope = host.Session.CreateScope(builder =>
                {
                    builder.RegisterInstance(siblingTrace);
                    builder.Add<RunningSibling>();
                });
                siblingStartup = host.InitializeScopeAsync(siblingScope, "unrelated-sibling");
                await Within(descendantTrace.Started.Task);
                await Within(siblingTrace.Started.Task);
                parentDisposal = parent.DisposeAsync().AsTask();
                await Within(descendantTrace.CleanupStarted.Task);
                Assert.That(descendantTrace.Cancelled, Is.True);
                Assert.That(hierarchy.ParentDisposed, Is.False);
                Assert.That(parentDisposal.IsCompleted, Is.False);
                Assert.That(siblingTrace.Cancelled, Is.False, "disposing one lineage must not cancel an unrelated sibling");
                Assert.That(siblingStartup.IsCompleted, Is.False);
                descendantTrace.ReleaseCleanup.TrySetResult(true);
                await Within(parentDisposal);
                Assert.That(hierarchy.ParentDisposedDuringCleanup, Is.False);
                Assert.That(hierarchy.ParentDisposed, Is.True);
                Assert.That(siblingTrace.Cancelled, Is.False);
            }
            finally
            {
                descendantTrace.ReleaseCleanup.TrySetResult(true);
                await Within(host.DisposeAsync().AsTask());
                if (parentDisposal != null) await Within(parentDisposal);
                if (descendantStartup != null) await IgnoreCancellation(descendantStartup);
                if (siblingStartup != null) await IgnoreCancellation(siblingStartup);
                unmanaged?.Dispose();
            }
        }

        private static async Task IgnoreCancellation(Task task)
        {
            try { await Within(task); }
            catch (OperationCanceledException) { }
        }

        public sealed class ParentDisposalTrace
        {
            public ScopeRun? Parent;
            public Task? Disposal;
            public int Initializations;
            public int Disposals;
        }

        public sealed class DisposesParentDuringConstruction : IAsyncInitializable, IAsyncDisposable
        {
            private readonly ParentDisposalTrace _trace;
            public DisposesParentDuringConstruction(ParentDisposalTrace trace)
            {
                _trace = trace;
                trace.Disposal = trace.Parent!.DisposeAsync().AsTask();
            }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _trace.Initializations++;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                _trace.Disposals++;
                return default;
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task GrandchildConstructorDisposingItsParentMustPreventItsInitialization()
        {
            var trace = new ParentDisposalTrace();
            var hierarchy = new HierarchyTrace();
            var host = new RuntimeFlowHost(builder =>
            {
                builder.RegisterInstance(trace);
                builder.RegisterInstance(hierarchy);
            }, _ => { }, TestScope.Options(new CapturingLogger()));
            try
            {
                await Within(host.StartAsync());
                var parentScope = host.Session.CreateScope(builder => builder.Add<ChildParentResource>());
                trace.Parent = await Within(host.InitializeScopeAsync(parentScope, "parent-child"));
                var grandchildScope = parentScope.CreateScope(builder => builder.Add<DisposesParentDuringConstruction>());
                var refusal = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(
                    () => Within(host.InitializeScopeAsync(grandchildScope, "constructing-grandchild")));
                Assert.That(refusal.Message, Does.Contain("a parent scope stopped"));
                Assert.That(trace.Initializations, Is.Zero);
                Assert.That(trace.Disposals, Is.EqualTo(1));
                await Within(trace.Disposal!);
            }
            finally
            {
                if (trace.Disposal != null) await Within(trace.Disposal);
                await Within(host.DisposeAsync().AsTask());
            }
        }

        private static TaskCompletionSource<bool> Signal()
            => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task Within(Task task)
        {
            var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.That(winner, Is.SameAs(task), "bounded lifecycle proof timed out");
            await task;
        }

        private static async Task<T> Within<T>(Task<T> task)
        {
            await Within((Task)task);
            return await task;
        }
    }
}
