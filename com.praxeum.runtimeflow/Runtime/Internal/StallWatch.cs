using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace RuntimeFlow.Internal
{
    /// <summary>
    /// The time base of timeouts and stall warnings: a process-wide clock that stops while the application
    /// is suspended — sent to the background on a mobile platform, or unfocused in a player that does not
    /// run in the background — so a player who leaves the game during loading does not come back to
    /// services that timed out while nothing could run. Wall-clock durations (<see cref="StartupResult.Elapsed"/>,
    /// <see cref="ServiceStatus.Elapsed"/>) keep counting. <see cref="Suspend"/> and <see cref="Resume"/> are
    /// the seam the Unity hook and the tests drive.
    /// </summary>
    internal static class AppSuspension
    {
        private static readonly object Gate = new object();
        private static readonly System.Diagnostics.Stopwatch Active = System.Diagnostics.Stopwatch.StartNew();

        static AppSuspension() => Application.focusChanged += OnFocusChanged;

        /// <summary>Time the application spent not suspended since this clock started; monotonic.</summary>
        public static TimeSpan ActiveTime
        {
            get { lock (Gate) return Active.Elapsed; }
        }

        /// <summary>True while the application is suspended and the clock is stopped.</summary>
        public static bool IsSuspended
        {
            get { lock (Gate) return !Active.IsRunning; }
        }

        /// <summary>Stops the clock; idempotent.</summary>
        public static void Suspend()
        {
            lock (Gate) Active.Stop();
        }

        /// <summary>Restarts the clock where it stopped; idempotent.</summary>
        public static void Resume()
        {
            lock (Gate) Active.Start();
        }

        /// <summary>
        /// Re-arms the hook at the start of every player-loop session: with domain reload disabled the
        /// static state survives, and a suspension left over from the previous session must not stop timeouts.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            Application.focusChanged -= OnFocusChanged;
            Application.focusChanged += OnFocusChanged;
            Resume();
        }

        private static void OnFocusChanged(bool focused)
        {
            if (focused)
            {
                Resume();
                return;
            }

            // The editor outside Play Mode keeps running whatever has focus (EditMode tests included).
            if (Application.isEditor && !Application.isPlaying) return;

            // A player that keeps running unfocused (a desktop build with Run In Background) still makes
            // progress, so its time counts. Mobile players never run in the background.
            if (Application.isMobilePlatform || !Application.runInBackground) Suspend();
        }
    }

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
