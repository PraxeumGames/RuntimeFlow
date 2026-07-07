using NUnit.Framework;
using System;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{
    public sealed partial class UxImprovementsTests
    {
        [Test]
        public void ScopeState_AfterDeclare_IsNotLoaded()
        {
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineGlobalScope();
                builder.DefineSessionScope();
            });

            Assert.That(pipeline.GetScopeState<GlobalScope>(), Is.EqualTo(ScopeLifecycleState.NotLoaded));
            Assert.That(pipeline.GetScopeState<SessionScope>(), Is.EqualTo(ScopeLifecycleState.NotLoaded));
            Assert.That(pipeline.GetScopeState<TestSceneScope>(), Is.EqualTo(ScopeLifecycleState.NotLoaded));
            Assert.That(pipeline.GetScopeState<TestModuleScope>(), Is.EqualTo(ScopeLifecycleState.NotLoaded));
        }

        [Test]
        public async Task ScopeState_AfterBuildAsync_GlobalAndSessionAreActive()
        {
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineGlobalScope();
                builder.DefineSessionScope();
                builder.Session().RegisterInstance<ITestSessionService>(
                    new AttemptControlledSessionService((_, _) => Task.CompletedTask));
            });

            await pipeline.InitializeAsync();

            Assert.That(pipeline.GetScopeState<GlobalScope>(), Is.EqualTo(ScopeLifecycleState.Active));
            Assert.That(pipeline.GetScopeState<SessionScope>(), Is.EqualTo(ScopeLifecycleState.Active));
        }

        [Test]
        public async Task ScopeState_AfterLoadSceneAndModule_AllScopesAreActive()
        {
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineGlobalScope();
                builder.DefineSessionScope();
                builder.Session().RegisterInstance<ITestSessionService>(
                    new AttemptControlledSessionService((_, _) => Task.CompletedTask));
                builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(
                    new AttemptControlledSceneService((_, _) => Task.CompletedTask))));
                builder.Module(new TestModuleScope(m => m.RegisterInstance<ITestModuleService>(
                    new AttemptControlledModuleService((_, _) => Task.CompletedTask))));
            });

            await pipeline.InitializeAsync();
            await pipeline.LoadSceneAsync<TestSceneScope>();
            await pipeline.LoadModuleAsync<TestModuleScope>();

            Assert.That(pipeline.GetScopeState<GlobalScope>(), Is.EqualTo(ScopeLifecycleState.Active));
            Assert.That(pipeline.GetScopeState<SessionScope>(), Is.EqualTo(ScopeLifecycleState.Active));
            Assert.That(pipeline.GetScopeState<TestSceneScope>(), Is.EqualTo(ScopeLifecycleState.Active));
            Assert.That(pipeline.GetScopeState<TestModuleScope>(), Is.EqualTo(ScopeLifecycleState.Active));
        }

        [Test]
        public async Task ScopeState_AfterReloadSession_SessionBecomesActive()
        {
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().RegisterInstance<ITestSessionService>(
                    new AttemptControlledSessionService((_, _) => Task.CompletedTask));
            });

            await pipeline.InitializeAsync();
            await pipeline.ReloadScopeAsync<SessionScope>();

            Assert.That(pipeline.GetScopeState<SessionScope>(), Is.EqualTo(ScopeLifecycleState.Active));
        }

        [Test]
        public async Task ScopeState_AfterFailedInit_IsFailed()
        {
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().RegisterInstance<ITestSessionService>(
                    new FailingSessionService());
            });

            await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.InitializeAsync());

            Assert.That(pipeline.GetScopeState<SessionScope>(), Is.EqualTo(ScopeLifecycleState.Failed));
        }

        [Test]
        public void ScopeState_UndeclaredScope_ReturnsNotLoaded()
        {
            var pipeline = RuntimePipeline.Create(_ => { });

            Assert.That(pipeline.GetScopeState<UndeclaredScope>(), Is.EqualTo(ScopeLifecycleState.NotLoaded));
        }
    }

}
