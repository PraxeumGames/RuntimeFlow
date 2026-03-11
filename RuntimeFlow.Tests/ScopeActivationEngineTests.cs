using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

public sealed class ScopeActivationEngineTests
{
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void DiscoverScopeActivationExecutionPlan_ReturnsDeterministicEnterAndReverseExitOrder()
    {
        var calls = new List<string>();
        var builder = new GameContextBuilder();
        var context = CreateSessionContext(
            new GammaSessionActivationService(calls),
            new AlphaSessionActivationService(calls),
            new BetaSessionActivationService(calls));

        var executionPlan = DiscoverExecutionPlan(builder, GameContextType.Session, context);
        var enterOrder = ReadServiceOrder(executionPlan, "EnterOrder");
        var exitOrder = ReadServiceOrder(executionPlan, "ExitOrder");

        Assert.Equal(
            new[] { typeof(IAlphaSessionActivationService), typeof(IBetaSessionActivationService), typeof(IGammaSessionActivationService) },
            enterOrder);
        Assert.Equal(
            new[] { typeof(IGammaSessionActivationService), typeof(IBetaSessionActivationService), typeof(IAlphaSessionActivationService) },
            exitOrder);
    }

    [Fact]
    public async Task ExecuteScopeActivationEnterAndExitAsync_InvokesHooksInDeterministicOrder()
    {
        var calls = new List<string>();
        var builder = new GameContextBuilder();
        var context = CreateSessionContext(
            new GammaSessionActivationService(calls),
            new AlphaSessionActivationService(calls),
            new BetaSessionActivationService(calls));

        await ExecuteScopeActivationPhaseAsync(builder, "ExecuteScopeActivationEnterAsync", context, CancellationToken.None);
        await ExecuteScopeActivationPhaseAsync(builder, "ExecuteScopeActivationExitAsync", context, CancellationToken.None);

        Assert.Equal(
            new[]
            {
                "enter:alpha",
                "enter:beta",
                "enter:gamma",
                "exit:gamma",
                "exit:beta",
                "exit:alpha"
            },
            calls);
    }

    [Fact]
    public async Task ExecuteScopeActivationEnterAsync_CancellationRequested_ThrowsBeforeInvocations()
    {
        var calls = new List<string>();
        var builder = new GameContextBuilder();
        var context = CreateSessionContext(
            new GammaSessionActivationService(calls),
            new AlphaSessionActivationService(calls),
            new BetaSessionActivationService(calls));
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExecuteScopeActivationPhaseAsync(
                builder,
                "ExecuteScopeActivationEnterAsync",
                context,
                cancellationSource.Token));

        Assert.Empty(calls);
    }

    [Fact]
    public async Task ExecuteScopeActivationEnterAsync_ServiceException_FailsFast()
    {
        var calls = new List<string>();
        var builder = new GameContextBuilder();
        var context = CreateSessionContext(
            new GammaSessionActivationService(calls),
            new AlphaThrowingSessionActivationService(calls),
            new BetaSessionActivationService(calls));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteScopeActivationPhaseAsync(builder, "ExecuteScopeActivationEnterAsync", context, CancellationToken.None));

        Assert.Equal("alpha-failed", exception.Message);
        Assert.Equal(new[] { "enter:alpha" }, calls);
    }

    [Fact]
    public async Task ExecuteScopeActivationExitAsync_ServiceException_FailsFast()
    {
        var calls = new List<string>();
        var builder = new GameContextBuilder();
        var context = CreateSessionContext(
            new GammaSessionActivationService(calls),
            new AlphaSessionActivationService(calls),
            new BetaThrowingOnExitSessionActivationService(calls));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteScopeActivationPhaseAsync(builder, "ExecuteScopeActivationExitAsync", context, CancellationToken.None));

        Assert.Equal("beta-exit-failed", exception.Message);
        Assert.Equal(new[] { "exit:gamma", "exit:beta" }, calls);
    }

    [Fact]
    public async Task ExecuteScopeActivationEnterAsync_CancellationDuringActivation_FailsFast()
    {
        var calls = new List<string>();
        var activationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new GameContextBuilder();
        var context = CreateSessionContext(
            new GammaSessionActivationService(calls),
            new AlphaBlockingSessionActivationService(calls, activationStarted),
            new BetaSessionActivationService(calls));
        using var cancellationSource = new CancellationTokenSource();

        var execution = ExecuteScopeActivationPhaseAsync(
            builder,
            "ExecuteScopeActivationEnterAsync",
            context,
            cancellationSource.Token);
        await activationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.Equal(new[] { "enter:alpha" }, calls);
    }

    [Fact]
    public async Task ExecuteScopeActivationExitAsync_CancellationDuringActivation_FailsFast()
    {
        var calls = new List<string>();
        var deactivationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new GameContextBuilder();
        var context = CreateSessionContext(
            new GammaBlockingOnExitSessionActivationService(calls, deactivationStarted),
            new AlphaSessionActivationService(calls),
            new BetaSessionActivationService(calls));
        using var cancellationSource = new CancellationTokenSource();

        var execution = ExecuteScopeActivationPhaseAsync(
            builder,
            "ExecuteScopeActivationExitAsync",
            context,
            cancellationSource.Token);
        await deactivationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.Equal(new[] { "exit:gamma" }, calls);
    }

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

    private static object DiscoverExecutionPlan(GameContextBuilder builder, GameContextType scope, GameContext context)
    {
        var method = typeof(GameContextBuilder).GetMethod(
                         "DiscoverScopeActivationExecutionPlan",
                         InstanceFlags,
                         binder: null,
                         types: new[] { typeof(GameContextType), typeof(GameContext) },
                         modifiers: null)
                     ?? throw new InvalidOperationException("Scope activation plan discovery method not found.");
        return InvokeMethod(method, builder, scope, context)
               ?? throw new InvalidOperationException("Scope activation plan discovery returned null.");
    }

    private static IReadOnlyList<Type> ReadServiceOrder(object executionPlan, string propertyName)
    {
        var orderProperty = executionPlan.GetType().GetProperty(propertyName, InstanceFlags)
                            ?? throw new InvalidOperationException($"Execution plan property '{propertyName}' not found.");
        if (orderProperty.GetValue(executionPlan) is not IEnumerable participants)
            throw new InvalidOperationException($"Execution plan property '{propertyName}' returned null.");

        var serviceTypes = new List<Type>();
        foreach (var participant in participants)
        {
            var participantType = participant?.GetType()
                                  ?? throw new InvalidOperationException("Scope activation participant is null.");
            var serviceTypeProperty = participantType.GetProperty("ServiceType", InstanceFlags)
                                      ?? throw new InvalidOperationException("Scope activation participant service type property not found.");
            if (serviceTypeProperty.GetValue(participant) is not Type serviceType)
                throw new InvalidOperationException("Scope activation participant service type value is null.");
            serviceTypes.Add(serviceType);
        }

        return serviceTypes;
    }

    private static async Task ExecuteScopeActivationPhaseAsync(
        GameContextBuilder builder,
        string methodName,
        GameContext context,
        CancellationToken cancellationToken)
    {
        var method = typeof(GameContextBuilder).GetMethod(
                         methodName,
                         InstanceFlags,
                         binder: null,
                         types: new[] { typeof(GameContextType), typeof(GameContext), typeof(CancellationToken) },
                         modifiers: null)
                     ?? throw new InvalidOperationException($"Scope activation method '{methodName}' not found.");
        var task = InvokeMethod(method, builder, GameContextType.Session, context, cancellationToken) as Task
                   ?? throw new InvalidOperationException($"Scope activation method '{methodName}' did not return a Task.");
        await task.ConfigureAwait(false);
    }

    private static object? InvokeMethod(MethodInfo method, object target, params object?[] args)
    {
        try
        {
            return method.Invoke(target, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private interface IAlphaSessionActivationService : ISessionScopeActivationService;
    private interface IBetaSessionActivationService : ISessionScopeActivationService;
    private interface IGammaSessionActivationService : ISessionScopeActivationService;

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
