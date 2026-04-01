using System;
using System.Collections.Generic;

namespace RuntimeFlow.Lifecycle
{
    public sealed class LifecycleStateEngine<TLifecycleKey>
    {
        private static readonly IReadOnlyCollection<LifecycleSnapshot<TLifecycleKey>> EmptySnapshots =
            Array.Empty<LifecycleSnapshot<TLifecycleKey>>();

        private readonly object _sync = new();
        private readonly Dictionary<TLifecycleKey, LifecycleSnapshot<TLifecycleKey>> _snapshots = new();
        private readonly Func<DateTimeOffset> _timestampProvider;

        public LifecycleStateEngine(Func<DateTimeOffset> timestampProvider = null)
        {
            _timestampProvider = timestampProvider ?? (() => DateTimeOffset.UtcNow);
        }

        public event Action<LifecycleSnapshot<TLifecycleKey>> SnapshotPublished;

        public bool TryGetSnapshot(TLifecycleKey lifecycleKey, out LifecycleSnapshot<TLifecycleKey> snapshot)
        {
            EnsureKey(lifecycleKey, nameof(lifecycleKey));

            lock (_sync)
            {
                if (_snapshots.TryGetValue(lifecycleKey, out var existing))
                {
                    snapshot = existing;
                    return true;
                }
            }

            snapshot = CreateUninitializedSnapshot(lifecycleKey);
            return false;
        }

        public LifecycleSnapshot<TLifecycleKey> GetSnapshotOrDefault(TLifecycleKey lifecycleKey)
        {
            TryGetSnapshot(lifecycleKey, out var snapshot);
            return snapshot;
        }

        public IReadOnlyCollection<LifecycleSnapshot<TLifecycleKey>> GetPublishedSnapshots()
        {
            lock (_sync)
            {
                if (_snapshots.Count == 0)
                    return EmptySnapshots;

                var copy = new LifecycleSnapshot<TLifecycleKey>[_snapshots.Count];
                var index = 0;
                foreach (var snapshot in _snapshots.Values)
                {
                    copy[index++] = snapshot;
                }

                return copy;
            }
        }

        public LifecycleSnapshot<TLifecycleKey> Publish(
            TLifecycleKey lifecycleKey,
            LifecycleState nextState,
            float progress,
            string reasonCode = null,
            string diagnostic = null,
            string errorType = null,
            string errorMessage = null)
        {
            return PublishInternal(
                lifecycleKey,
                expectedCurrentState: null,
                nextState,
                progress,
                reasonCode,
                diagnostic,
                errorType,
                errorMessage,
                validateExpectedState: false,
                out _);
        }

        public bool TryPublish(
            TLifecycleKey lifecycleKey,
            LifecycleState expectedCurrentState,
            LifecycleState nextState,
            float progress,
            out LifecycleSnapshot<TLifecycleKey> snapshot,
            string reasonCode = null,
            string diagnostic = null,
            string errorType = null,
            string errorMessage = null)
        {
            snapshot = PublishInternal(
                lifecycleKey,
                expectedCurrentState,
                nextState,
                progress,
                reasonCode,
                diagnostic,
                errorType,
                errorMessage,
                validateExpectedState: true,
                out var published);

            return published;
        }

        private LifecycleSnapshot<TLifecycleKey> PublishInternal(
            TLifecycleKey lifecycleKey,
            LifecycleState? expectedCurrentState,
            LifecycleState nextState,
            float progress,
            string reasonCode,
            string diagnostic,
            string errorType,
            string errorMessage,
            bool validateExpectedState,
            out bool published)
        {
            EnsureKey(lifecycleKey, nameof(lifecycleKey));

            Action<LifecycleSnapshot<TLifecycleKey>> handlers = null;
            LifecycleSnapshot<TLifecycleKey> snapshot;

            lock (_sync)
            {
                _snapshots.TryGetValue(lifecycleKey, out var currentSnapshot);
                var previousState = currentSnapshot?.State ?? LifecycleState.Uninitialized;

                if (validateExpectedState && previousState != expectedCurrentState)
                {
                    snapshot = currentSnapshot ?? CreateUninitializedSnapshot(lifecycleKey);
                    published = false;
                    return snapshot;
                }

                var timestamp = _timestampProvider();
                var transition = new LifecycleTransition<TLifecycleKey>(
                    lifecycleKey,
                    previousState,
                    nextState,
                    timestamp,
                    reasonCode,
                    diagnostic);

                snapshot = new LifecycleSnapshot<TLifecycleKey>(
                    lifecycleKey,
                    nextState,
                    progress,
                    timestamp,
                    reasonCode,
                    diagnostic,
                    errorType,
                    errorMessage,
                    transition);

                _snapshots[lifecycleKey] = snapshot;
                handlers = SnapshotPublished;
            }

            handlers?.Invoke(snapshot);
            published = true;
            return snapshot;
        }

        private static LifecycleSnapshot<TLifecycleKey> CreateUninitializedSnapshot(TLifecycleKey lifecycleKey)
        {
            return new LifecycleSnapshot<TLifecycleKey>(
                lifecycleKey,
                LifecycleState.Uninitialized,
                0f,
                DateTimeOffset.MinValue);
        }

        private static void EnsureKey(TLifecycleKey lifecycleKey, string parameterName)
        {
            if (ReferenceEquals(lifecycleKey, null))
                throw new ArgumentNullException(parameterName);
        }
    }
}
