using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public sealed class NullScopeTransitionHandler : IScopeTransitionHandler
    {
        public static readonly IScopeTransitionHandler Instance = new NullScopeTransitionHandler();

        private NullScopeTransitionHandler() { }

        public Task OnTransitionOutAsync(ScopeTransitionContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task OnTransitionProgressAsync(ScopeTransitionContext context, float progress, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task OnTransitionInAsync(ScopeTransitionContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
