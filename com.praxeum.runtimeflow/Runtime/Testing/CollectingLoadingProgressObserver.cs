using System.Collections.Generic;
using RuntimeFlow.Loading;

namespace RuntimeFlow.Testing
{
    /// <summary>Records every loading-progress snapshot for assertions.</summary>
    public sealed class CollectingLoadingProgressObserver : IRuntimeLoadingProgressObserver
    {
        private readonly List<RuntimeLoadingOperationSnapshot> _snapshots = new();

        public IReadOnlyList<RuntimeLoadingOperationSnapshot> Snapshots => _snapshots;

        public void OnLoadingProgress(RuntimeLoadingOperationSnapshot snapshot)
            => _snapshots.Add(snapshot);
    }
}
