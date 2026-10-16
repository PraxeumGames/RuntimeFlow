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
    /// <summary>
    /// Teardown reads VContainer's private lifetime state. When that layout cannot be read, an owned
    /// scope must still be released — the graph's async services in dependency order, then plain
    /// container disposal — instead of leaking the container and every disposable in it.
    /// </summary>
    [TestFixture]
    public sealed class TeardownAdapterFallbackTests
    {
        private const string AdapterFailure = "lifetime adapter failed";

        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
        }

        [TearDown]
        public void TearDown() => VContainerLifetimeTracker.CaptureHook = null;

        public sealed class Journal
        {
            public readonly List<string> Events = new List<string>();
        }

        public sealed class AsyncResource : IAsyncInitializable, IAsyncDisposable
        {
            private readonly Journal _journal;
            public AsyncResource(Journal journal) => _journal = journal;
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync() { _journal.Events.Add("async-resource"); return default; }
        }

        public sealed class AsyncDependent : IAsyncInitializable, IAsyncDisposable
        {
            private readonly Journal _journal;
            public AsyncDependent(Journal journal, AsyncResource resource) { _journal = journal; GC.KeepAlive(resource); }
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync() { _journal.Events.Add("async-dependent"); return default; }
        }

        public sealed class SyncHelper : IDisposable
        {
            private readonly Journal _journal;
            public SyncHelper(Journal journal) => _journal = journal;
            public void Dispose() => _journal.Events.Add("sync-helper");
        }

        [DependsOn(typeof(CycleB))]
        public sealed class CycleA : IAsyncInitializable, IAsyncDisposable
        {
            private readonly Journal _journal;
            public CycleA(Journal journal) => _journal = journal;
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync() { _journal.Events.Add("cycle-a"); return default; }
        }

        [DependsOn(typeof(CycleA))]
        public sealed class CycleB : IAsyncInitializable, IAsyncDisposable
        {
            private readonly Journal _journal;
            public CycleB(Journal journal) => _journal = journal;
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync() { _journal.Events.Add("cycle-b"); return default; }
        }

        private static void FailCaptureOf(IObjectResolver scope)
            => VContainerLifetimeTracker.CaptureHook = resolver =>
            {
                if (ReferenceEquals(resolver, scope)) throw new InvalidOperationException("simulated VContainer layout change");
            };

        private int AdapterFailures() => _log.Messages(LogLevel.Error).Count(message => message.Contains(AdapterFailure));

        [Test]
        [Timeout(10000)]
        public async Task AnOwnedRunReleasesItsServicesAndScopeWhenTheLifetimeAdapterFails()
        {
            var journal = new Journal();
            var scope = TestScope.Build(builder =>
            {
                builder.RegisterInstance(journal);
                builder.Add<AsyncResource>();
                builder.Add<AsyncDependent>();
                builder.Register<SyncHelper>(Lifetime.Singleton);
            });
            scope.Resolve<SyncHelper>();
            var run = ScopeRun.Create(scope, "fallback", _options, ownsScope: true);
            await run.RunAsync();

            FailCaptureOf(scope);
            await run.DisposeAsync();

            Assert.That(journal.Events, Is.EqualTo(new[] { "async-dependent", "async-resource", "sync-helper" }), _log.Dump());
            Assert.That(AdapterFailures(), Is.EqualTo(1), _log.Dump());
        }

        [Test]
        [Timeout(10000)]
        public async Task AFailedOwnedBuildReleasesItsServicesAndScopeWhenTheLifetimeAdapterFails()
        {
            var journal = new Journal();
            var scope = TestScope.Build(builder =>
            {
                builder.RegisterInstance(journal);
                builder.Add<CycleA>();
                builder.Add<CycleB>();
                builder.Register<SyncHelper>(Lifetime.Singleton);
            });
            scope.Resolve<SyncHelper>();
            FailCaptureOf(scope);

            var error = await AsyncTestAssert.ThrowsAsync<InitGraphException>(() =>
                ScopeRun.CreateAsync(scope, "fallback", _options, ownsScope: true));

            Assert.That(error.Message, Does.Contain("cycle"), "the graph error stays the primary failure");
            Assert.That(journal.Events, Does.Contain("cycle-a").And.Contain("cycle-b"), _log.Dump());
            Assert.That(journal.Events.Last(), Is.EqualTo("sync-helper"), "the scope is disposed after the async services\n" + _log.Dump());
            Assert.That(AdapterFailures(), Is.EqualTo(1), _log.Dump());
        }

        [Test]
        [Timeout(10000)]
        public async Task ABorrowedRunStillReleasesItsAsyncServicesWhenTheLifetimeAdapterFails()
        {
            var journal = new Journal();
            var scope = TestScope.Build(builder =>
            {
                builder.RegisterInstance(journal);
                builder.Add<AsyncResource>();
                builder.Add<AsyncDependent>();
                builder.Register<SyncHelper>(Lifetime.Singleton);
            });
            try
            {
                scope.Resolve<SyncHelper>();
                var run = ScopeRun.Create(scope, "fallback", _options);
                await run.RunAsync();

                FailCaptureOf(scope);
                await run.DisposeAsync();

                Assert.That(journal.Events, Is.EqualTo(new[] { "async-dependent", "async-resource" }),
                    "the caller still owns the scope and its synchronous cleanup\n" + _log.Dump());
                Assert.That(AdapterFailures(), Is.EqualTo(1), _log.Dump());
            }
            finally
            {
                VContainerLifetimeTracker.CaptureHook = null;
                scope.Dispose();
            }
        }
    }
}
