using NUnit.Framework;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{
    public sealed class SessionRestartPreparationHookWiringTests
    {
        [Test]
        public async Task RestartSessionAsync_ExecutesConfiguredPreparationHooksBeforeSessionRestart()
        {
            var events = new List<string>();
            var pipeline = RuntimePipeline.Create(
                builder =>
                {
                    builder.DefineSessionScope();
                    builder.Session().RegisterInstance<ITestSessionService>(
                        new AttemptControlledSessionService((attempt, _) =>
                        {
                            events.Add($"session-init:{attempt}");
                            return Task.CompletedTask;
                        }));
                },
                options =>
                {
                    options.SessionRestartPreparationHooks = new[] { new RecordingHook(events) };
                });

            await pipeline.InitializeAsync();
            events.Clear();

            await pipeline.RestartSessionAsync();

            Assert.That(
                events,
                Is.EqualTo(new[]
                {
                    "hook",
                    "session-init:2"
                }));
        }

        [Test]
        public async Task ConfigureGuards_KeepsPreparationHooksAheadOfRuntimeGuards()
        {
            var events = new List<string>();
            var pipeline = RuntimePipeline.Create(
                builder =>
                {
                    builder.DefineSessionScope();
                    builder.Session().RegisterInstance<ITestSessionService>(
                        new AttemptControlledSessionService((attempt, _) =>
                        {
                            events.Add($"session-init:{attempt}");
                            return Task.CompletedTask;
                        }));
                },
                options =>
                {
                    options.SessionRestartPreparationHooks = new[] { new RecordingHook(events) };
                });

            pipeline.ConfigureGuards(new RecordingGuard(events));

            await pipeline.InitializeAsync();
            events.Clear();

            await pipeline.RestartSessionAsync();

            Assert.That(
                events,
                Is.EqualTo(new[]
                {
                    "hook",
                    "guard",
                    "session-init:2"
                }));
        }

        private sealed class RecordingHook : IRuntimeSessionRestartPreparationHook
        {
            private readonly IList<string> _events;

            public RecordingHook(IList<string> events)
            {
                _events = events;
            }

            public Task PrepareForSessionRestartAsync(
                RuntimeSessionRestartPreparationContext context,
                CancellationToken cancellationToken = default)
            {
                _events.Add("hook");
                return Task.CompletedTask;
            }
        }

        private sealed class RecordingGuard : IRuntimeFlowGuard
        {
            private readonly IList<string> _events;

            public RecordingGuard(IList<string> events)
            {
                _events = events;
            }

            public Task<RuntimeFlowGuardResult> EvaluateAsync(
                RuntimeFlowGuardContext context,
                CancellationToken cancellationToken = default)
            {
                if (context.Stage == RuntimeFlowGuardStage.BeforeSessionRestart)
                {
                    _events.Add("guard");
                }

                return Task.FromResult(RuntimeFlowGuardResult.Allow());
            }
        }
    }

}
