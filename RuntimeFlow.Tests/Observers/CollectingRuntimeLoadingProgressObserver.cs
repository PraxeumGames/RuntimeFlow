using System.Collections.Generic;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

internal sealed class CollectingRuntimeLoadingProgressObserver : IRuntimeLoadingProgressObserver
{
    public List<RuntimeLoadingOperationSnapshot> Snapshots { get; } = new();

    public void OnLoadingProgress(RuntimeLoadingOperationSnapshot snapshot)
    {
        Snapshots.Add(snapshot);
    }
}
