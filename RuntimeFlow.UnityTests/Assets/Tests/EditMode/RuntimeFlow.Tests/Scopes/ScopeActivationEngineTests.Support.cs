using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{

public sealed partial class ScopeActivationEngineTests
{
    private static readonly ScopeActivationService ActivationService =
        new(InlineInitializationExecutionScheduler.Instance);

    private static GameContext CreateSessionContext(
        IGammaSessionActivationService gamma,
        IAlphaSessionActivationService alpha,
        IBetaSessionActivationService beta)
    {
        var context = new GameContext();
        context.RegisterInstance<IGammaSessionActivationService>(gamma);
        context.RegisterInstance<IAlphaSessionActivationService>(alpha);
        context.RegisterInstance<IBetaSessionActivationService>(beta);
        context.Initialize();
        return context;
    }

    private static ScopeActivationExecutionPlan DiscoverExecutionPlan(GameContextBuilder builder, GameContextType scope, GameContext context)
        => ActivationService.DiscoverPlan(scope, context);

    private static IReadOnlyList<Type> ReadServiceOrder(ScopeActivationExecutionPlan executionPlan, string propertyName)
        => (propertyName == "EnterOrder" ? executionPlan.EnterOrder : executionPlan.ExitOrder)
            .Select(participant => participant.ServiceType)
            .ToArray();

    private static Task ExecuteScopeActivationPhaseAsync(
        GameContextBuilder builder,
        string methodName,
        GameContext context,
        CancellationToken cancellationToken)
        => methodName == "ExecuteScopeActivationEnterAsync"
            ? ActivationService.ExecuteEnterAsync(GameContextType.Session, context, NullInitializationProgressNotifier.Instance, totalServices: 0, cancellationToken)
            : ActivationService.ExecuteExitAsync(GameContextType.Session, context, NullInitializationProgressNotifier.Instance, cancellationToken);

    private interface IAlphaSessionActivationService : ISessionScopeActivationService { }    private interface IBetaSessionActivationService : ISessionScopeActivationService { }    private interface IGammaSessionActivationService : ISessionScopeActivationService { }
    private abstract class SessionActivationServiceBase : ISessionScopeActivationService
    {
        private readonly string _name;
        private readonly List<string> _calls;

        protected SessionActivationServiceBase(string name, List<string> calls)
        {
            _name = name;
            _calls = calls;
        }

        public virtual Task OnScopeActivatedAsync(CancellationToken cancellationToken)
        {
            _calls.Add($"enter:{_name}");
            return Task.CompletedTask;
        }

        public virtual Task OnScopeDeactivatingAsync(CancellationToken cancellationToken)
        {
            _calls.Add($"exit:{_name}");
            return Task.CompletedTask;
        }
    }

    private sealed class AlphaSessionActivationService : SessionActivationServiceBase, IAlphaSessionActivationService
    {
        public AlphaSessionActivationService(List<string> calls) : base("alpha", calls)
        {
        }
    }

    private sealed class AlphaThrowingSessionActivationService : SessionActivationServiceBase, IAlphaSessionActivationService
    {
        public AlphaThrowingSessionActivationService(List<string> calls) : base("alpha", calls)
        {
        }

        public override Task OnScopeActivatedAsync(CancellationToken cancellationToken)
        {
            base.OnScopeActivatedAsync(cancellationToken);
            throw new InvalidOperationException("alpha-failed");
        }
    }

    private sealed class AlphaBlockingSessionActivationService : SessionActivationServiceBase, IAlphaSessionActivationService
    {
        private readonly TaskCompletionSource<bool> _activationStarted;

        public AlphaBlockingSessionActivationService(List<string> calls, TaskCompletionSource<bool> activationStarted)
            : base("alpha", calls)
        {
            _activationStarted = activationStarted;
        }

        public override async Task OnScopeActivatedAsync(CancellationToken cancellationToken)
        {
            await base.OnScopeActivatedAsync(cancellationToken).ConfigureAwait(false);
            _activationStarted.TrySetResult(true);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class BetaSessionActivationService : SessionActivationServiceBase, IBetaSessionActivationService
    {
        public BetaSessionActivationService(List<string> calls) : base("beta", calls)
        {
        }
    }

    private sealed class BetaThrowingOnExitSessionActivationService : SessionActivationServiceBase, IBetaSessionActivationService
    {
        public BetaThrowingOnExitSessionActivationService(List<string> calls) : base("beta", calls)
        {
        }

        public override Task OnScopeDeactivatingAsync(CancellationToken cancellationToken)
        {
            base.OnScopeDeactivatingAsync(cancellationToken);
            throw new InvalidOperationException("beta-exit-failed");
        }
    }

    private sealed class GammaSessionActivationService : SessionActivationServiceBase, IGammaSessionActivationService
    {
        public GammaSessionActivationService(List<string> calls) : base("gamma", calls)
        {
        }
    }

    private sealed class GammaBlockingOnExitSessionActivationService : SessionActivationServiceBase, IGammaSessionActivationService
    {
        private readonly TaskCompletionSource<bool> _deactivationStarted;

        public GammaBlockingOnExitSessionActivationService(List<string> calls, TaskCompletionSource<bool> deactivationStarted)
            : base("gamma", calls)
        {
            _deactivationStarted = deactivationStarted;
        }

        public override async Task OnScopeDeactivatingAsync(CancellationToken cancellationToken)
        {
            await base.OnScopeDeactivatingAsync(cancellationToken).ConfigureAwait(false);
            _deactivationStarted.TrySetResult(true);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }
}

}
