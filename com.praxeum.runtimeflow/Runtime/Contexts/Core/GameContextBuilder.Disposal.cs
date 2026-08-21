using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder
    {
        private async Task DisposeContextAsync(GameContext? context, CancellationToken cancellationToken)
        {
            if (context == null) return;
            await context.DisposeAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task DisposeContextAsync(IGameContext? context, CancellationToken cancellationToken)
        {
            if (context == null)
                return;

            await _executionScheduler.ExecuteAsync(
                    InitializationThreadAffinity.MainThread,
                    _ =>
                    {
                        context.Dispose();
                        return Task.CompletedTask;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static CancellationToken CreateFailureCleanupCancellationToken()
        {
            return CancellationToken.None;
        }

        private Task DisposeScopeContextAsync(
            GameContextType scope,
            GameContext? context,
            CancellationToken cancellationToken,
            Type? scopeKey = null,
            Action? onDisposed = null)
            => _disposalService.DisposeScopeContextAsync(scope, context, cancellationToken, scopeKey, onDisposed);

        private Task DisposeActivatedScopeAsync(
            GameContextType scope,
            GameContext? context,
            IInitializationProgressNotifier progressNotifier,
            CancellationToken cancellationToken,
            Type? scopeKey = null,
            ScopeLifecycleState? transitionState = null)
            => _scopeTransitions.ExitActivatedScopeAsync(
                scope,
                context,
                scopeKey,
                transitionState,
                progressNotifier,
                cancellationToken,
                () => { });

        private Task DisposeOwnedGlobalContextAsync(IGameContext? context, CancellationToken cancellationToken)
            => _disposalService.DisposeOwnedGlobalContextAsync(context, cancellationToken);

        private void DisposeAndClearEventBuses(bool includeGlobal)
            => ScopeCleanupFailures.DisposeAndClearEventBuses(_activeState, includeGlobal);
    }
}
