using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public sealed class ScopeTransitionContext
    {
        public ScopeTransitionContext(
            GameContextType sourceScope,
            Type? sourceScopeKey,
            GameContextType targetScope,
            Type targetScopeKey)
        {
            SourceScope = sourceScope;
            SourceScopeKey = sourceScopeKey;
            TargetScope = targetScope;
            TargetScopeKey = targetScopeKey;
        }

        public GameContextType SourceScope { get; }
        public Type? SourceScopeKey { get; }
        public GameContextType TargetScope { get; }
        public Type TargetScopeKey { get; }
    }

    public interface IScopeTransitionHandler
    {
        Task OnTransitionOutAsync(ScopeTransitionContext context, CancellationToken cancellationToken = default);
        Task OnTransitionProgressAsync(ScopeTransitionContext context, float progress, CancellationToken cancellationToken = default);
        Task OnTransitionInAsync(ScopeTransitionContext context, CancellationToken cancellationToken = default);
    }
}
