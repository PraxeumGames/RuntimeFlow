using NUnit.Framework;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{
    public sealed partial class UxImprovementsTests
    {
        [Test]
        public async Task IsScopeActive_ReturnsTrue_AfterInitialization()
        {
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().RegisterInstance<ITestSessionService>(
                    new AttemptControlledSessionService((_, _) => Task.CompletedTask));
            });

            await pipeline.InitializeAsync();

            Assert.That(pipeline.IsScopeActive<SessionScope>(), Is.True);
        }

        [Test]
        public void IsScopeActive_ReturnsFalse_BeforeInitialization()
        {
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
            });

            Assert.That(pipeline.IsScopeActive<SessionScope>(), Is.False);
        }

        [Test]
        public async Task CanReloadScope_ReturnsTrue_ForActiveSessionSceneModule()
        {
            var pipeline = RuntimePipeline.Create(builder =>
            {
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

            Assert.That(pipeline.CanReloadScope<SessionScope>(), Is.True);
            Assert.That(pipeline.CanReloadScope<TestSceneScope>(), Is.True);
            Assert.That(pipeline.CanReloadScope<TestModuleScope>(), Is.True);
        }

        [Test]
        public async Task CanReloadScope_ReturnsFalse_ForGlobalScope()
        {
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineGlobalScope();
            });

            await pipeline.InitializeAsync();

            Assert.That(pipeline.CanReloadScope<GlobalScope>(), Is.False);
        }

        [Test]
        public void CanReloadScope_ReturnsFalse_ForNonActiveScope()
        {
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
            });

            Assert.That(pipeline.CanReloadScope<SessionScope>(), Is.False);
        }

        [Test]
        public async Task CanReloadScope_ReturnsFalse_AfterDispose()
        {
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().RegisterInstance<ITestSessionService>(
                    new AttemptControlledSessionService((_, _) => Task.CompletedTask));
            });

            await pipeline.InitializeAsync();
            await pipeline.DisposeAsync();

            Assert.That(pipeline.CanReloadScope<SessionScope>(), Is.False);
        }
    }

}
