using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Internal;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;

namespace RuntimeFlow.Tests.Lifecycle
{
    [TestFixture]
    public sealed class ChildDisposalContractTests
    {
        public sealed class CleanupTrace
        {
            public List<string> Events { get; } = new List<string>();
            public TaskCompletionSource<bool> ChildDisposalStarted { get; } = Signal();
            public TaskCompletionSource<bool> ReleaseChild { get; } = Signal();
        }

        public sealed class SessionResource : IAsyncInitializable, IDisposable
        {
            private readonly CleanupTrace _trace;
            public bool Disposed { get; private set; }

            public SessionResource(CleanupTrace trace) => _trace = trace;
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;

            public void Dispose()
            {
                Disposed = true;
                _trace.Events.Add("session.dispose");
            }
        }

        public sealed class ChildWithAsyncCleanup : IAsyncInitializable, IAsyncDisposable
        {
            private readonly CleanupTrace _trace;
            public SessionResource Parent { get; }
            public int Initializations { get; private set; }
            public bool ParentWasDisposedDuringCleanup { get; private set; }

            public ChildWithAsyncCleanup(CleanupTrace trace, SessionResource parent)
            {
                _trace = trace;
                Parent = parent;
            }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                Initializations++;
                return Task.CompletedTask;
            }

            public async ValueTask DisposeAsync()
            {
                _trace.Events.Add("child.dispose.start");
                _trace.ChildDisposalStarted.TrySetResult(true);
                await _trace.ReleaseChild.Task;
                ParentWasDisposedDuringCleanup = Parent.Disposed;
                _trace.Events.Add("child.dispose.finish");
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        [Timeout(10000)]
        public async Task ParentTeardownRetainsAndAwaitsAnAlreadyDisposingChild(bool restart)
        {
            var trace = new CleanupTrace();
            var host = CreateHost(trace);
            Task? childDisposal = null;
            Task? parentOperation = null;
            ChildWithAsyncCleanup? service = null;
            try
            {
                await Within(host.StartAsync());
                var parent = host.Session.Resolve<SessionResource>();
                var scope = host.Session.CreateScope(builder => builder.Add<ChildWithAsyncCleanup>());
                var child = await Within(host.InitializeScopeAsync(scope, "lobby"));
                service = scope.Resolve<ChildWithAsyncCleanup>();
                Assert.That(service.Parent, Is.SameAs(parent));

                childDisposal = child.DisposeAsync().AsTask();
                await Within(trace.ChildDisposalStarted.Task);
                Assert.That(host.ChildRuns, Does.Contain(child), "pending cleanup remains owned by the host");
                parentOperation = restart
                    ? (Task)host.RestartAsync("child-cleanup-order")
                    : host.DisposeAsync().AsTask();
                // Both host operations defer exactly once onto this same main-thread context before
                // tearing down an idle generation. Let that queued turn reach the child's open gate.
                await Task.Yield();
                Assert.That(parentOperation.IsCompleted, Is.False, "the host operation must join the pending child teardown");
                Assert.That(parent.Disposed, Is.False, "the child still holds its live session dependency");
            }
            finally
            {
                // Release before any cleanup await, including when an assertion above failed.
                trace.ReleaseChild.TrySetResult(true);
                if (childDisposal != null) await Within(childDisposal);
                if (parentOperation != null) await Within(parentOperation);
                await Within(host.DisposeAsync().AsTask());
            }

            Assert.That(service!.ParentWasDisposedDuringCleanup, Is.False, string.Join(" -> ", trace.Events));
            Assert.That(trace.Events.IndexOf("child.dispose.finish"), Is.LessThan(trace.Events.IndexOf("session.dispose")));
            Assert.That(host.ChildRuns, Is.Empty, "finished cleanup releases the host's strong child tracking");
        }

        [TestCase(false)]
        [TestCase(true)]
        [Timeout(10000)]
        public async Task AChildResolverCannotInitializeTwiceDuringOrAfterDisposal(bool afterDisposal)
        {
            var trace = new CleanupTrace();
            var host = CreateHost(trace);
            Task? disposal = null;
            try
            {
                await Within(host.StartAsync());
                var scope = host.Session.CreateScope(builder => builder.Add<ChildWithAsyncCleanup>());
                var first = await Within(host.InitializeScopeAsync(scope, "lobby"));
                var service = scope.Resolve<ChildWithAsyncCleanup>();
                disposal = first.DisposeAsync().AsTask();
                if (afterDisposal)
                {
                    trace.ReleaseChild.TrySetResult(true);
                    await Within(disposal);
                    Assert.That(host.ChildRuns, Is.Empty);
                }

                var rejection = await AsyncTestAssert.ThrowsAsync<InitGraphException>(
                    () => Within(host.InitializeScopeAsync(scope, "again")));
                Assert.That(rejection.Message, Does.Contain("has already been initialized as 'lobby'"));
                Assert.That(service.Initializations, Is.EqualTo(1));
            }
            finally
            {
                trace.ReleaseChild.TrySetResult(true);
                if (disposal != null) await Within(disposal);
                await Within(host.DisposeAsync().AsTask());
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        [Timeout(10000)]
        public async Task ADescendantOfADisposingOrDisposedChildCannotBypassItsParent(bool afterDisposal)
        {
            var trace = new CleanupTrace();
            var host = CreateHost(trace);
            Task? disposal = null;
            IScopedObjectResolver? descendant = null;
            try
            {
                await Within(host.StartAsync());
                var scope = host.Session.CreateScope(builder => builder.Add<ChildWithAsyncCleanup>());
                var first = await Within(host.InitializeScopeAsync(scope, "lobby"));
                // Keep the descendant alive across removal of its parent from the live child list.
                descendant = scope.CreateScope(_ => { });
                disposal = first.DisposeAsync().AsTask();
                if (afterDisposal)
                {
                    trace.ReleaseChild.TrySetResult(true);
                    await Within(disposal);
                    Assert.That(host.ChildRuns, Is.Empty);
                }

                var rejection = await AsyncTestAssert.ThrowsAsync<InitGraphException>(
                    () => Within(host.InitializeScopeAsync(descendant, "match")));
                Assert.That(rejection.Message, Does.Contain("child scope 'lobby' that is disposed or disposing"));
            }
            finally
            {
                trace.ReleaseChild.TrySetResult(true);
                if (disposal != null) await Within(disposal);
                descendant?.Dispose();
                await Within(host.DisposeAsync().AsTask());
            }
        }

        public sealed class ConstructorReentry
        {
            public RuntimeFlowHost Host = null!;
            public IScopedObjectResolver? Descendant;
            public Task<ScopeRun>? Attempt;
            public int DescendantInitializations;
        }

        public sealed class CreatesDescendantWhileConstructing : IAsyncInitializable
        {
            public CreatesDescendantWhileConstructing(ConstructorReentry trace, IObjectResolver resolver)
            {
                trace.Descendant = resolver.CreateScope(builder => builder.Add<DescendantInitializer>());
                trace.Attempt = trace.Host.InitializeScopeAsync(trace.Descendant, "constructor-grandchild");
            }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        public sealed class DescendantInitializer : IAsyncInitializable
        {
            private readonly ConstructorReentry _trace;
            public DescendantInitializer(ConstructorReentry trace) => _trace = trace;

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _trace.DescendantInitializations++;
                return Task.CompletedTask;
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task AConstructorCannotStartItsDescendantBeforeItsOwnParentRunIsPublishedAndCompleted()
        {
            var trace = new ConstructorReentry();
            var host = new RuntimeFlowHost(builder => builder.RegisterInstance(trace), _ => { },
                TestScope.Options(new CapturingLogger()));
            trace.Host = host;
            try
            {
                await Within(host.StartAsync());
                await Within(host.InitializeScopeAsync(
                    host.Session.CreateScope(builder => builder.Add<CreatesDescendantWhileConstructing>()), "constructing-parent"));

                Assert.That(trace.Attempt, Is.Not.Null, "the constructor attempted to initialize its descendant");
                var rejection = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(() => Within(trace.Attempt!));
                Assert.That(rejection.Message, Does.Contain("parent scope 'constructing-parent' is still being constructed"));
                Assert.That(rejection.Message, Does.Contain("after the parent's InitializeScopeAsync has completed"));
                Assert.That(trace.DescendantInitializations, Is.Zero, "the descendant must not bypass an unpublished parent run");

                // Precondition refusal leaves the descendant caller-owned and usable once its parent is ready.
                var descendant = await Within(host.InitializeScopeAsync(trace.Descendant!, "ready-grandchild"));
                Assert.That(descendant.State, Is.EqualTo(RunState.Completed));
                Assert.That(trace.DescendantInitializations, Is.EqualTo(1));
            }
            finally
            {
                await Within(host.DisposeAsync().AsTask());
                trace.Descendant?.Dispose();
                if (trace.Attempt != null)
                {
                    try { await Within(trace.Attempt); }
                    catch (InvalidOperationException) { }
                }
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task AChildCanInitializeAcrossAnOrdinaryUnmanagedAncestor()
        {
            var trace = new ConstructorReentry();
            var host = new RuntimeFlowHost(builder => builder.RegisterInstance(trace), _ => { },
                TestScope.Options(new CapturingLogger()));
            IScopedObjectResolver? unmanaged = null;
            try
            {
                await Within(host.StartAsync());
                unmanaged = host.Session.CreateScope(_ => { });
                var descendant = await Within(host.InitializeScopeAsync(
                    unmanaged.CreateScope(builder => builder.Add<DescendantInitializer>()), "managed-grandchild"));

                Assert.That(descendant.State, Is.EqualTo(RunState.Completed));
                Assert.That(trace.DescendantInitializations, Is.EqualTo(1));
            }
            finally
            {
                await Within(host.DisposeAsync().AsTask());
                unmanaged?.Dispose();
            }
        }

        public sealed class SharedDisposalFailure
        {
            public Exception Error { get; } = new InvalidOperationException("shared failure");
            public List<string> Attempts { get; } = new List<string>();
        }

        public sealed class ThrowsSharedFailureA : IDisposable
        {
            private readonly SharedDisposalFailure _failure;
            public ThrowsSharedFailureA(SharedDisposalFailure failure) => _failure = failure;
            public void Dispose()
            {
                _failure.Attempts.Add("A");
                throw _failure.Error;
            }
        }

        public sealed class ThrowsSharedFailureB : IDisposable
        {
            private readonly SharedDisposalFailure _failure;
            public ThrowsSharedFailureB(SharedDisposalFailure failure) => _failure = failure;
            public void Dispose()
            {
                _failure.Attempts.Add("B");
                throw _failure.Error;
            }
        }

        public sealed class DisposalRecorder : IDisposable
        {
            public int Calls { get; private set; }
            public void Dispose() => Calls++;
        }

        [Test]
        public void VContainerDisposalContinuesAfterTwoRegistrationsThrowTheSameExceptionObject()
        {
            var failure = new SharedDisposalFailure();
            var logger = new CapturingLogger();
            var scope = TestScope.Build(builder =>
            {
                builder.RegisterInstance(failure);
                builder.Register<DisposalRecorder>(Lifetime.Singleton);
                builder.Register<ThrowsSharedFailureA>(Lifetime.Singleton);
                builder.Register<ThrowsSharedFailureB>(Lifetime.Singleton);
            });
            try
            {
                // VContainer disposes in reverse resolution order: B throws, A throws, recorder runs.
                var recorder = scope.Resolve<DisposalRecorder>();
                scope.Resolve<ThrowsSharedFailureA>();
                scope.Resolve<ThrowsSharedFailureB>();

                ScopeDisposal.Dispose(scope, logger, "shared-failure");

                Assert.That(failure.Attempts, Is.EqualTo(new[] { "B", "A" }));
                Assert.That(recorder.Calls, Is.EqualTo(1), logger.Dump());
                Assert.That(logger.Has(LogLevel.Error, "no progress"), Is.False, logger.Dump());
                Assert.That(logger.Has(LogLevel.Error, "retry limit"), Is.False, logger.Dump());
            }
            finally
            {
                ScopeDisposal.Dispose(scope, logger, "shared-failure cleanup");
            }
        }

        public sealed class ReentrantTeardown
        {
            public RuntimeFlowHost Host = null!;
            public bool DisposeHost;
            public bool TokenCancelledSynchronously;
            public Task? Disposal;
            public TaskCompletionSource<bool> Resumed { get; } = Signal();
        }

        public sealed class ChildAwaitingItsOwnTeardown : IAsyncInitializable
        {
            private readonly ReentrantTeardown _trace;
            public ChildAwaitingItsOwnTeardown(ReentrantTeardown trace) => _trace = trace;

            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _trace.Disposal = _trace.DisposeHost
                    ? _trace.Host.DisposeAsync().AsTask()
                    : _trace.Host.ChildRuns.Single().DisposeAsync().AsTask();
                _trace.TokenCancelledSynchronously = cancellationToken.IsCancellationRequested;
                await _trace.Disposal;
                _trace.Resumed.TrySetResult(true);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        [Timeout(10000)]
        public async Task AChildInitializerAwaitingItsOwnOrItsHostsDisposalResumesAfterTheCancellationGrace(bool disposeHost)
        {
            var trace = new ReentrantTeardown { DisposeHost = disposeHost };
            var options = TestScope.Options(new CapturingLogger());
            options.CancellationGrace = TimeSpan.FromMilliseconds(50);
            var host = new RuntimeFlowHost(builder => builder.RegisterInstance(trace), _ => { }, options);
            trace.Host = host;
            Task<ScopeRun>? startup = null;
            try
            {
                await Within(host.StartAsync());
                startup = host.InitializeScopeAsync(host.Session.CreateScope(builder => builder.Add<ChildAwaitingItsOwnTeardown>()), "self-disposal");
                await Within(trace.Resumed.Task);
                Assert.That(trace.TokenCancelledSynchronously, Is.False);
                Assert.That(host.ChildRuns, Is.Empty);
            }
            finally
            {
                if (trace.Disposal != null) await Within(trace.Disposal);
                await Within(host.DisposeAsync().AsTask());
                if (startup != null)
                {
                    try { await Within(startup); }
                    catch (OperationCanceledException) { }
                    catch (ObjectDisposedException) { }
                }
            }
        }

        private static RuntimeFlowHost CreateHost(CleanupTrace trace) => new RuntimeFlowHost(
            builder => builder.RegisterInstance(trace),
            builder => builder.Add<SessionResource>(),
            TestScope.Options(new CapturingLogger()));

        private static TaskCompletionSource<bool> Signal()
            => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task Within(Task task)
        {
            Assert.That(await Task.WhenAny(task, Task.Delay(3000)), Is.SameAs(task), "The lifecycle operation did not settle.");
            await task;
        }

        private static async Task<T> Within<T>(Task<T> task)
        {
            await Within((Task)task);
            return await task;
        }
    }
}
