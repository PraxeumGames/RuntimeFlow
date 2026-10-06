using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Internal
{
    /// <summary>
    /// The single periodic loop of a run: it drives timeout enforcement and the stall warning.
    /// It runs on the captured synchronization context and stops with the run's token.
    /// </summary>
    internal sealed class StallWatch
    {
        private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(10);
        private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

        private readonly TimeSpan _interval;
        private readonly Action _onTick;
        private readonly Action<Exception> _onTickFailed;
        private CancellationTokenSource? _cts;

        public StallWatch(TimeSpan interval, Action onTick, Action<Exception> onTickFailed)
        {
            _interval = interval;
            _onTick = onTick;
            _onTickFailed = onTickFailed;
        }

        /// <summary>Interval short enough to honour the smallest configured stall or timeout threshold.</summary>
        public static TimeSpan IntervalFor(RuntimeFlowOptions options, IReadOnlyList<ServiceNode> nodes)
        {
            var interval = DefaultInterval;
            if (options.StallWarningAfter > TimeSpan.Zero)
                interval = Min(interval, Divide(options.StallWarningAfter));

            if (options.TimeoutMultiplier > 0)
            {
                for (var i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];
                    if (node.UserGated || node.TimeoutSeconds <= 0) continue;
                    // Compared in seconds: a huge timeout must not overflow TimeSpan on its way to the minimum.
                    var quarter = node.TimeoutSeconds * options.TimeoutMultiplier / 4;
                    if (quarter < interval.TotalSeconds) interval = TimeSpan.FromSeconds(quarter);
                }
            }

            return interval < MinimumInterval ? MinimumInterval : interval;
        }

        /// <summary>Starts ticking until <paramref name="token"/> is cancelled or <see cref="Stop"/> is called.</summary>
        public void Start(CancellationToken token)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _ = LoopAsync(_cts.Token);
        }

        /// <summary>Stops the loop; safe to call more than once.</summary>
        public void Stop()
        {
            var cts = _cts;
            _cts = null;
            if (cts == null) return;
            cts.Cancel();
            cts.Dispose();
        }

        private async Task LoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_interval, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (token.IsCancellationRequested) return;

                // The loop is fire-and-forget: an exception escaping here would silently end every later
                // timeout and stall warning of the run, so a failing tick is reported and the loop goes on.
                try { _onTick(); }
                catch (Exception exception) { _onTickFailed(exception); }
            }
        }

        private static TimeSpan Divide(TimeSpan value) => TimeSpan.FromTicks(value.Ticks / 4);

        private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    }
}
