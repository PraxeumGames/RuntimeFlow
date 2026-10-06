using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Tests.VContainerIntegration
{
    /// <summary>
    /// Who receives an entry-point exception, on upstream VContainer 1.15.3 and on the 1.19 fork, whose
    /// <c>EnsureDispatcherRegistered</c> registers <c>Debug.LogException</c> as a default handler in every
    /// scope that has none: the host's collector must still fail a build whose IInitializable throws,
    /// and a consumer handler must still win.
    /// </summary>
    [TestFixture]
    public sealed class EntryPointHandlerTests
    {
        private static readonly Type HandlerType =
            typeof(EntryPointsBuilder).Assembly.GetType("VContainer.Unity.EntryPointExceptionHandler")!;

        public sealed class ThrowingEntryPoint : IInitializable
        {
            public void Initialize() => throw new InvalidOperationException("entry point exploded");
        }

        public sealed class GlobalService : AutoService { }

        public sealed class SessionService : AutoService { }

        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
        }

        private static void Publish(IObjectResolver scope, Exception error)
        {
            var handler = scope.Resolve(HandlerType);
            HandlerType.GetMethod("Publish")!.Invoke(handler, new object[] { error });
        }

        [Test]
        [Timeout(10000)]
        public async Task AGlobalInstallerUsingUseEntryPointsStillFailsOnAThrowingInitializable()
        {
            await using var host = new RuntimeFlowHost(
                builder =>
                {
                    builder.Add<GlobalService>();
                    builder.UseEntryPoints(entryPoints => entryPoints.Add<ThrowingEntryPoint>());
                },
                builder => builder.Add<SessionService>(),
                _options);

            var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => host.StartAsync());

            Assert.That(failure.Scope, Is.EqualTo("global"));
            Assert.That(failure.Service, Is.EqualTo(nameof(ThrowingEntryPoint)));
            Assert.That(_log.Has(LogLevel.Information, "custom EntryPointExceptionHandler"), Is.False,
                "VContainer's own default handler is not a consumer handler\n" + _log.Dump());
        }

        [Test]
        [Timeout(10000)]
        public async Task ASessionBelowAContainerWithTheDefaultLogExceptionHandlerStillFails()
        {
            // What a root LifetimeScope looks like: the dispatcher registered, and (on VContainer 1.19,
            // or explicitly here) Debug.LogException as the entry-point exception handler.
            var root = TestScope.Build(builder =>
            {
                builder.Add<GlobalService>();
                EntryPointsBuilder.EnsureDispatcherRegistered(builder);
                if (!builder.Exists(HandlerType)) builder.RegisterEntryPointExceptionHandler(UnityEngine.Debug.LogException);
            });
            try
            {
                await using var host = RuntimeFlowHost.From(root, builder =>
                {
                    builder.Add<SessionService>();
                    builder.RegisterEntryPoint<ThrowingEntryPoint>();
                }, _options);

                var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => host.StartAsync());

                Assert.That(failure.Scope, Is.EqualTo("session"));
                Assert.That(failure.Service, Is.EqualTo(nameof(ThrowingEntryPoint)));
            }
            finally
            {
                root.Dispose();
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task ASessionBelowARootWhoseDispatcherVContainerRegisteredStillFails()
        {
            var root = TestScope.Build(builder =>
            {
                builder.Add<GlobalService>();
                EntryPointsBuilder.EnsureDispatcherRegistered(builder);
            });
            try
            {
                await using var host = RuntimeFlowHost.From(root, builder =>
                {
                    builder.Add<SessionService>();
                    builder.RegisterEntryPoint<ThrowingEntryPoint>();
                }, _options);

                var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => host.StartAsync());

                Assert.That(failure.Service, Is.EqualTo(nameof(ThrowingEntryPoint)));
            }
            finally
            {
                root.Dispose();
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task AConsumerHandlerOfAnExternalRootReceivesTheSessionsEntryPointExceptions()
        {
            var handled = new List<Exception>();
            var root = TestScope.Build(builder =>
            {
                builder.Add<GlobalService>();
                builder.RegisterEntryPointExceptionHandler(handled.Add);
                EntryPointsBuilder.EnsureDispatcherRegistered(builder);
            });
            try
            {
                await using var host = RuntimeFlowHost.From(root, builder =>
                {
                    builder.Add<SessionService>();
                    builder.RegisterEntryPoint<ThrowingEntryPoint>();
                }, _options);

                var result = await host.StartAsync();

                Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
                Assert.That(handled.Select(e => e.Message), Is.EqualTo(new[] { "entry point exploded" }));
            }
            finally
            {
                root.Dispose();
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task APlayerLoopExceptionInTheSessionReachesTheConsumerHandlerOfGlobal()
        {
            var handled = new List<Exception>();
            await using var host = new RuntimeFlowHost(
                builder => builder.RegisterEntryPointExceptionHandler(handled.Add),
                builder => builder.Add<SessionService>(),
                _options);
            await host.StartAsync();

            Publish(host.Session, new InvalidOperationException("tick exploded"));

            Assert.That(handled.Select(e => e.Message), Is.EqualTo(new[] { "tick exploded" }));
            Assert.That(_log.Has(LogLevel.Error, "tick exploded"), Is.False, _log.Dump());
        }

        [Test]
        [Timeout(10000)]
        public async Task APlayerLoopExceptionInTheSessionIsLoggedWithoutAConsumerHandler()
        {
            await using var host = new RuntimeFlowHost(builder => { }, builder => builder.Add<SessionService>(), _options);
            await host.StartAsync();

            Publish(host.Session, new InvalidOperationException("tick exploded"));

            Assert.That(_log.Has(LogLevel.Error, "threw InvalidOperationException in a VContainer entry point after its scope was built: tick exploded"),
                Is.True, _log.Dump());
            var restart = await host.RestartAsync("after-tick");
            Assert.That(restart.Outcome, Is.EqualTo(StartupOutcome.Completed), "a player-loop exception must not fail the next build");
        }
    }
}
