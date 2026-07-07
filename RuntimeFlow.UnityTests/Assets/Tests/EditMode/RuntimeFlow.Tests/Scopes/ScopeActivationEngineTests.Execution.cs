using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{

public sealed partial class ScopeActivationEngineTests
{
    [Test]
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

        Assert.AreEqual(
            new[] { typeof(IAlphaSessionActivationService), typeof(IBetaSessionActivationService), typeof(IGammaSessionActivationService) },
            enterOrder);
        Assert.AreEqual(
            new[] { typeof(IGammaSessionActivationService), typeof(IBetaSessionActivationService), typeof(IAlphaSessionActivationService) },
            exitOrder);
    }

    [Test]
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

        Assert.AreEqual(
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

    [Test]
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

        await AsyncTestAssert.CatchAsync<OperationCanceledException>(() =>
            ExecuteScopeActivationPhaseAsync(
                builder,
                "ExecuteScopeActivationEnterAsync",
                context,
                cancellationSource.Token));

        Assert.IsEmpty(calls);
    }

    [Test]
    public async Task ExecuteScopeActivationEnterAsync_ServiceException_FailsFast()
    {
        var calls = new List<string>();
        var builder = new GameContextBuilder();
        var context = CreateSessionContext(
            new GammaSessionActivationService(calls),
            new AlphaThrowingSessionActivationService(calls),
            new BetaSessionActivationService(calls));

        var exception = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteScopeActivationPhaseAsync(builder, "ExecuteScopeActivationEnterAsync", context, CancellationToken.None));

        Assert.AreEqual("alpha-failed", exception.Message);
        Assert.AreEqual(new[] { "enter:alpha" }, calls);
    }

    [Test]
    public async Task ExecuteScopeActivationExitAsync_ServiceException_FailsFast()
    {
        var calls = new List<string>();
        var builder = new GameContextBuilder();
        var context = CreateSessionContext(
            new GammaSessionActivationService(calls),
            new AlphaSessionActivationService(calls),
            new BetaThrowingOnExitSessionActivationService(calls));

        var exception = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteScopeActivationPhaseAsync(builder, "ExecuteScopeActivationExitAsync", context, CancellationToken.None));

        Assert.AreEqual("beta-exit-failed", exception.Message);
        Assert.AreEqual(new[] { "exit:gamma", "exit:beta" }, calls);
    }

    [Test]
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

        await AsyncTestAssert.CatchAsync<OperationCanceledException>(() => execution);

        Assert.AreEqual(new[] { "enter:alpha" }, calls);
    }

    [Test]
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

        await AsyncTestAssert.CatchAsync<OperationCanceledException>(() => execution);

        Assert.AreEqual(new[] { "exit:gamma" }, calls);
    }
}

}
