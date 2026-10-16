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
    public sealed class HostWeightedProgressTests
    {
        public sealed class ProgressTrace
        {
            public TaskCompletionSource<bool> Started { get; } = Signal();
            public TaskCompletionSource<bool> Release { get; } = Signal();
            public InitContext? Context;
        }

        [Init(Weight = double.MaxValue)]
        public sealed class MaximumWeightGlobal : IAsyncInitializable
        {
            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        [Init(Weight = double.MaxValue)]
        public sealed class MaximumWeightSession : IAsyncInitializable
        {
            private readonly ProgressTrace _trace;
            public MaximumWeightSession(ProgressTrace trace) => _trace = trace;
            public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                _trace.Context = context;
                _trace.Started.TrySetResult(true);
                await _trace.Release.Task;
            }
        }

        [Test]
        [Timeout(10000)]
        public async Task MaximumFiniteWeightsAcrossScopesKeepHostPercentageFiniteAndAccurate()
        {
            var trace = new ProgressTrace();
            var host = new RuntimeFlowHost(
                builder =>
                {
                    builder.RegisterInstance(trace);
                    builder.Add<MaximumWeightGlobal>();
                },
                builder => builder.Add<MaximumWeightSession>(), TestScope.Options(new CapturingLogger()));
            Task<StartupResult>? startup = null;
            try
            {
                startup = host.StartAsync();
                await Within(trace.Started.Task);
                AssertPercent(host, 50);
                trace.Context!.ReportProgress(0.5f);
                AssertPercent(host, 75);
                trace.Release.TrySetResult(true);
                await Within(startup);
                AssertPercent(host, 100);
            }
            finally
            {
                trace.Release.TrySetResult(true);
                if (startup != null) await Within(startup);
                await Within(host.DisposeAsync().AsTask());
            }
        }

        private static void AssertPercent(RuntimeFlowHost host, double expected)
        {
            var actual = host.GetStatus().Percent;
            Assert.That(double.IsNaN(actual) || double.IsInfinity(actual), Is.False);
            Assert.That(actual, Is.EqualTo(expected).Within(0.00001));
        }

        private static TaskCompletionSource<bool> Signal()
            => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task Within(Task task)
        {
            var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.That(winner, Is.SameAs(task), "host progress regression timed out");
            await task;
        }
    }
}
