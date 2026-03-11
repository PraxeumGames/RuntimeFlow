using System;
using System.Collections.Generic;

namespace RuntimeFlow.Contexts
{
    public sealed class InMemoryRuntimeHealthBaselineStore : IRuntimeHealthBaselineStore
    {
        private readonly object _sync = new();
        private readonly Dictionary<(string device, GameContextType scope, Type service), BaselineEntry> _entries = new();

        public static InMemoryRuntimeHealthBaselineStore Shared { get; } = new();

        public bool TryGetBaseline(string deviceProfile, GameContextType scope, Type serviceType, out TimeSpan baseline)
        {
            lock (_sync)
            {
                if (_entries.TryGetValue((deviceProfile, scope, serviceType), out var entry))
                {
                    baseline = TimeSpan.FromMilliseconds(entry.EwmaMs);
                    return true;
                }
            }

            baseline = default;
            return false;
        }

        public void Record(string deviceProfile, GameContextType scope, Type serviceType, TimeSpan duration)
        {
            var key = (deviceProfile, scope, serviceType);
            lock (_sync)
            {
                if (!_entries.TryGetValue(key, out var entry))
                {
                    _entries[key] = new BaselineEntry(duration.TotalMilliseconds, samples: 1);
                    return;
                }

                const double alpha = 0.25d;
                var next = alpha * duration.TotalMilliseconds + (1d - alpha) * entry.EwmaMs;
                _entries[key] = new BaselineEntry(next, entry.Samples + 1);
            }
        }

        private readonly struct BaselineEntry
        {
            public BaselineEntry(double ewmaMs, int samples)
            {
                EwmaMs = ewmaMs;
                Samples = samples;
            }

            public double EwmaMs { get; }
            public int Samples { get; }
        }
    }
}
