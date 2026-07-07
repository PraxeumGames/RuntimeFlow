using NUnit.Framework;
using System;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{
    public sealed partial class UxImprovementsTests
    {
        [Test]
        public void ScopeNotDeclaredException_HasCorrectScopeType()
        {
            var exception = new ScopeNotDeclaredException(typeof(UndeclaredScope));

            Assert.That(exception.ScopeType, Is.EqualTo(typeof(UndeclaredScope)));
        }

        [Test]
        public void ScopeNotRestartableException_HasCorrectMessage()
        {
            var exception = new ScopeNotRestartableException(typeof(GlobalScope));

            Assert.That(exception.ScopeType, Is.EqualTo(typeof(GlobalScope)));
        }

        [Test]
        public void FlowNotConfiguredException_HasCorrectMessage()
        {
            var exception = new FlowNotConfiguredException();

            Assert.That(exception.Message, Does.Contain("ConfigureFlow"));
        }

        [Test]
        public void ScopeRegistrationException_HasDiagnosticCode()
        {
            var exception = new ScopeRegistrationException("GBSR3001", "Duplicate scope declaration.");

            Assert.That(exception.DiagnosticCode, Is.EqualTo("GBSR3001"));
        }

        [Test]
        public async Task ReloadScopeAsync_UndeclaredScope_ThrowsScopeNotDeclaredException()
        {
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineSessionScope();
                builder.Session().RegisterInstance<ITestSessionService>(
                    new AttemptControlledSessionService((_, _) => Task.CompletedTask));
            });

            await pipeline.InitializeAsync();

            var exception = await AsyncTestAssert.ThrowsAsync<ScopeNotDeclaredException>(async () =>
                await pipeline.ReloadScopeAsync<UndeclaredScope>());

            Assert.That(exception.ScopeType, Is.EqualTo(typeof(UndeclaredScope)));
        }

        [Test]
        public async Task ReloadScopeAsync_GlobalScope_ThrowsScopeNotRestartableException()
        {
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineGlobalScope();
            });

            await pipeline.InitializeAsync();

            var exception = await AsyncTestAssert.ThrowsAsync<ScopeNotRestartableException>(async () =>
                await pipeline.ReloadScopeAsync<GlobalScope>());

            Assert.That(exception.ScopeType, Is.EqualTo(typeof(GlobalScope)));
        }

        [Test]
        public async Task RunAsync_WithoutConfigureFlow_ThrowsFlowNotConfiguredException()
        {
            var pipeline = RuntimePipeline.Create(_ => { });

            await AsyncTestAssert.ThrowsAsync<FlowNotConfiguredException>(async () =>
                await pipeline.RunAsync(NoopSceneLoader.Instance));
        }
    }

}
