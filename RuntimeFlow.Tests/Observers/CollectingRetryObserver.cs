using System.Collections.Concurrent;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

internal sealed class CollectingRetryObserver : IRuntimeRetryObserver
{
    private readonly ConcurrentQueue<RuntimeRetryDecision> _decisions = new();

    public RuntimeRetryDecision[] Decisions => _decisions.ToArray();

    public void OnRetryDecision(RuntimeRetryDecision decision)
    {
        _decisions.Enqueue(decision);
    }
}
