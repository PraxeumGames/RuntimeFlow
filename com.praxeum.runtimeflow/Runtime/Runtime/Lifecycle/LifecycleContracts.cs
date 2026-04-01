using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Lifecycle
{
    public enum LifecycleState
    {
        Uninitialized = 0,
        Entering = 1,
        Active = 2,
        Exiting = 3,
        Inactive = 4,
        Failed = 5,
        Disposed = 6
    }

    public sealed class LifecycleTransition<TLifecycleKey>
    {
        public LifecycleTransition(
            TLifecycleKey lifecycleKey,
            LifecycleState previousState,
            LifecycleState currentState,
            DateTimeOffset timestampUtc,
            string reasonCode = null,
            string diagnostic = null)
        {
            if (lifecycleKey == null)
                throw new ArgumentNullException(nameof(lifecycleKey));

            LifecycleKey = lifecycleKey;
            PreviousState = previousState;
            CurrentState = currentState;
            TimestampUtc = timestampUtc;
            ReasonCode = Normalize(reasonCode);
            Diagnostic = Normalize(diagnostic);
        }

        public TLifecycleKey LifecycleKey { get; }
        public LifecycleState PreviousState { get; }
        public LifecycleState CurrentState { get; }
        public DateTimeOffset TimestampUtc { get; }
        public string ReasonCode { get; }
        public string Diagnostic { get; }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public sealed class LifecycleSnapshot<TLifecycleKey>
    {
        public LifecycleSnapshot(
            TLifecycleKey lifecycleKey,
            LifecycleState state,
            float progress,
            DateTimeOffset timestampUtc,
            string reasonCode = null,
            string diagnostic = null,
            string errorType = null,
            string errorMessage = null,
            LifecycleTransition<TLifecycleKey> lastTransition = null)
        {
            if (lifecycleKey == null)
                throw new ArgumentNullException(nameof(lifecycleKey));
            if (float.IsNaN(progress) || float.IsInfinity(progress) || progress < 0f || progress > 1f)
                throw new ArgumentOutOfRangeException(nameof(progress), progress, "Progress must be in [0..1].");

            LifecycleKey = lifecycleKey;
            State = state;
            Progress = progress;
            TimestampUtc = timestampUtc;
            ReasonCode = Normalize(reasonCode);
            Diagnostic = Normalize(diagnostic);
            ErrorType = Normalize(errorType);
            ErrorMessage = Normalize(errorMessage);
            LastTransition = lastTransition;
        }

        public TLifecycleKey LifecycleKey { get; }
        public LifecycleState State { get; }
        public float Progress { get; }
        public DateTimeOffset TimestampUtc { get; }
        public string ReasonCode { get; }
        public string Diagnostic { get; }
        public string ErrorType { get; }
        public string ErrorMessage { get; }
        public LifecycleTransition<TLifecycleKey> LastTransition { get; }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public interface ILifecycleSnapshotObserver<TLifecycleKey>
    {
        void OnSnapshot(LifecycleSnapshot<TLifecycleKey> snapshot);
    }

    internal sealed class NullLifecycleSnapshotObserver<TLifecycleKey> : ILifecycleSnapshotObserver<TLifecycleKey>
    {
        public static readonly ILifecycleSnapshotObserver<TLifecycleKey> Instance = new NullLifecycleSnapshotObserver<TLifecycleKey>();

        private NullLifecycleSnapshotObserver()
        {
        }

        public void OnSnapshot(LifecycleSnapshot<TLifecycleKey> snapshot)
        {
        }
    }

    public interface ILifecycleTransitions<in TTransitionContext>
    {
        Task OnTransitionOutAsync(TTransitionContext context, CancellationToken cancellationToken = default);
        Task OnTransitionProgressAsync(TTransitionContext context, float progress, CancellationToken cancellationToken = default);
        Task OnTransitionInAsync(TTransitionContext context, CancellationToken cancellationToken = default);
    }

    public sealed class NullLifecycleTransitions<TTransitionContext> : ILifecycleTransitions<TTransitionContext>
    {
        public static readonly ILifecycleTransitions<TTransitionContext> Instance = new NullLifecycleTransitions<TTransitionContext>();

        private NullLifecycleTransitions()
        {
        }

        public Task OnTransitionOutAsync(TTransitionContext context, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task OnTransitionProgressAsync(TTransitionContext context, float progress, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task OnTransitionInAsync(TTransitionContext context, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
