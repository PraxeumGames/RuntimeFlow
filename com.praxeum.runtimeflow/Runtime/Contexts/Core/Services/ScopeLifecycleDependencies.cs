using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Teardown and state collaborators shared by scope lifecycle operations.
    /// Bundled once per owner (builder or orchestrator) instead of re-spelling
    /// a delegate tuple at every call site.
    /// </summary>
    internal sealed class ScopeLifecycleDependencies
    {
        public Action<GameContextType, ScopeLifecycleState, Type?> SetState { get; set; } = static (_, _, _) => { };
        public Action<long, CancellationToken> ThrowIfStale { get; set; } = static (_, _) => { };
        public Func<GameContextType, GameContext?, CancellationToken, Type?, Action?, Task> DisposeScope { get; set; } =
            static (_, _, _, _, _) => Task.CompletedTask;
        public Func<CancellationToken, Func<Task>[], Task<List<Exception>>> CaptureCleanup { get; set; } =
            static (_, _) => Task.FromResult(new List<Exception>());
        public Func<string, Exception, IReadOnlyCollection<Exception>, AggregateException> CreateAggregate { get; set; } =
            static (operation, exception, _) => new AggregateException($"{operation} failed.", exception);
        public Func<CancellationToken> FailureCleanupToken { get; set; } = static () => CancellationToken.None;
        public Func<Exception, CancellationToken, bool> IsStaleCancellation { get; set; } =
            static (exception, cancellationToken) => exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;
    }
}
