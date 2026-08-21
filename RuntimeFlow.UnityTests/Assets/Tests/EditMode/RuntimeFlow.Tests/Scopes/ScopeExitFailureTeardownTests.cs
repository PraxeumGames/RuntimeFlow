using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using RuntimeFlow.Pipeline;

namespace RuntimeFlow.Tests
{
    public sealed class ScopeExitFailureTeardownTests
    {
        private interface IFailingExitSceneService : ISceneInitializableService, ISceneDisposableService, ISceneScopeActivationService
        {
        }

        private interface IFailingExitSessionService : ISessionInitializableService, ISessionDisposableService, ISessionScopeActivationService
        {
        }

        private sealed class FailingExitSceneService : IFailingExitSceneService
        {
            public int DisposeCount { get; private set; }

            public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task OnScopeActivatedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task OnScopeDeactivatingAsync(CancellationToken cancellationToken)
                => throw new InvalidOperationException("scene exit failed");

            public Task DisposeAsync(CancellationToken cancellationToken)
            {
                DisposeCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class FailingExitSessionService : IFailingExitSessionService
        {
            public int ExitCalls { get; private set; }

            public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task OnScopeActivatedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task OnScopeDeactivatingAsync(CancellationToken cancellationToken)
            {
                ExitCalls++;
                if (ExitCalls == 1)
                    throw new InvalidOperationException("session exit failed");

                return Task.CompletedTask;
            }

            public Task DisposeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        [Test]
        public async Task SceneExitHookFailure_StillDisposesScopeServices()
        {
            var sceneService = new FailingExitSceneService();
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Scene(new TestSceneScope(s => s.RegisterInstance<IFailingExitSceneService>(sceneService)));
            });

            await pipeline.InitializeAsync();
            await pipeline.LoadSceneAsync<TestSceneScope>();

            var exception = await AsyncTestAssert.ThrowsAsync<AggregateException>(
                () => pipeline.ReloadScopeAsync<TestSceneScope>());

            Assert.That(
                exception!.InnerExceptions,
                Has.Some.Matches<Exception>(inner =>
                    inner is InvalidOperationException invalid
                    && invalid.Message == "scene exit failed"));
            Assert.That(sceneService.DisposeCount, Is.EqualTo(1), "Scope services must be disposed even when the deactivation hook fails.");
        }

        [Test]
        public async Task SceneExitHookFailure_StillClearsActiveSceneContext()
        {
            var sceneService = new FailingExitSceneService();
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Scene(new TestSceneScope(s => s.RegisterInstance<IFailingExitSceneService>(sceneService)));
            });

            await pipeline.InitializeAsync();
            await pipeline.LoadSceneAsync<TestSceneScope>();

            await AsyncTestAssert.ThrowsAsync<AggregateException>(
                () => pipeline.ReloadScopeAsync<TestSceneScope>());

            // The failed load must not leave a stale scene context behind: a follow-up
            // scene load must succeed instead of trying to tear down an already-disposed scope.
            await AsyncTestAssert.DoesNotThrowAsync(() => pipeline.LoadSceneAsync<TestSceneScope>());
        }

        [Test]
        public async Task SessionRestart_ExitHookFailure_DoesNotLeaveStaleSessionContext()
        {
            var sessionService = new FailingExitSessionService();
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().RegisterInstance<IFailingExitSessionService>(sessionService);
            });

            await pipeline.InitializeAsync();

            var exception = await AsyncTestAssert.ThrowsAsync<AggregateException>(
                () => pipeline.ReloadScopeAsync<SessionScope>());

            Assert.That(
                exception!.InnerExceptions,
                Has.Some.Matches<Exception>(inner =>
                    inner is InvalidOperationException invalid
                    && invalid.Message == "session exit failed"));

            // A failed restart must not leave a stale (disposed) session context behind.
            Assert.That(() => pipeline.SessionContext, Throws.TypeOf<InvalidOperationException>());

            // The pipeline recovers: a follow-up restart starts from a clean slate.
            await AsyncTestAssert.DoesNotThrowAsync(() => pipeline.ReloadScopeAsync<SessionScope>());
            Assert.That(pipeline.SessionContext, Is.Not.Null);
            Assert.That(sessionService.ExitCalls, Is.EqualTo(1), "The recovery restart has no stale session to exit.");
        }
    }
}