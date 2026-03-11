using System.Collections.Concurrent;
using System.Threading;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

internal sealed class CollectingHealthObserver : IRuntimeHealthObserver
{
    private int _recoveryTriggeredCount;
    private readonly ConcurrentQueue<RuntimeHealthAnomaly> _anomalies = new();

    public int RecoveryTriggeredCount => Volatile.Read(ref _recoveryTriggeredCount);
    public RuntimeHealthAnomaly[] Anomalies => _anomalies.ToArray();

    public void OnServiceMetric(RuntimeServiceHealthMetric metric)
    {
    }

    public void OnAnomaly(RuntimeHealthAnomaly anomaly)
    {
        _anomalies.Enqueue(anomaly);
    }

    public void OnRecoveryTriggered(RuntimeHealthAnomaly anomaly, int attempt, int maxAttempts)
    {
        _anomalies.Enqueue(anomaly);
        Interlocked.Increment(ref _recoveryTriggeredCount);
    }
}
