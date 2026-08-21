using System;
using System.Collections.Generic;
using VContainer;
using VContainer.Internal;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Single ownership ledger for a GameContext: the shared-instance cache keyed by
    /// VContainer <see cref="Registration"/> plus the chronological list of owned
    /// IDisposable instances. Disposal walks the owned list in reverse so dependents
    /// tear down before their dependencies.
    /// </summary>
    internal sealed class GameContextInstanceLedger
    {
        private readonly Dictionary<Registration, object> _sharedInstances = new();
        private readonly List<object> _ownedInstances = new();
        private readonly HashSet<object> _ownedLookup = new(ReferenceEqualityComparer.Instance);

        public bool TryGetShared(Registration registration, out object instance)
            => _sharedInstances.TryGetValue(registration, out instance!);

        public void AddShared(Registration registration, object instance)
            => _sharedInstances[registration] = instance;

        public void TrackOwned(object instance)
        {
            if (instance is not IDisposable) return;
            if (_ownedLookup.Add(instance))
                _ownedInstances.Add(instance);
        }

        public bool HasOwnedInstances => _ownedInstances.Count > 0;

        public void DisposeOwnedReverse(List<Exception> failures)
        {
            for (var i = _ownedInstances.Count - 1; i >= 0; i--)
            {
                if (_ownedInstances[i] is not IDisposable disposable) continue;
                try { disposable.Dispose(); }
                catch (Exception ex) { failures.Add(ex); }
            }
        }

        public void ClearShared()
        {
            _sharedInstances.Clear();
        }

        public void Clear()
        {
            _sharedInstances.Clear();
            _ownedInstances.Clear();
            _ownedLookup.Clear();
        }
    }
}
