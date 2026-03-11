using System;
using System.Collections.Generic;
using System.Linq;
using RuntimeFlow.Contexts;
using Xunit;

namespace RuntimeFlow.Tests;

public sealed class RuntimeLoadingProgressNotifierAdapterTests
{
    [Fact]
    public void Adapter_MapsScopeAndServiceCallbacks_ToRuntimeLoadingSnapshots()
    {
        var observer = new CollectingRuntimeLoadingProgressObserver();
        var adapter = new RuntimeLoadingProgressNotifierAdapter(
            observer,
            RuntimeLoadingOperationKind.Initialize,
            operationId: "op-initialize",
            timestampProvider: static () => DateTimeOffset.UnixEpoch);

        adapter.OnScopeStarted(GameContextType.Session, totalServices: 2);
        adapter.OnServiceStarted(GameContextType.Session, typeof(ServiceA), completedServices: 0, totalServices: 2);
        adapter.OnServiceCompleted(GameContextType.Session, typeof(ServiceA), completedServices: 1, totalServices: 2);
        adapter.OnScopeCompleted(GameContextType.Session, totalServices: 2);

        Assert.Collection(
            observer.Snapshots,
            scopeStarted =>
            {
                Assert.Equal(RuntimeLoadingOperationStage.ScopeInitializing, scopeStarted.Stage);
                Assert.Equal(RuntimeLoadingOperationState.Running, scopeStarted.State);
                Assert.Equal(0d, scopeStarted.Percent);
                Assert.Equal(0, scopeStarted.CurrentStep);
                Assert.Equal(2, scopeStarted.TotalSteps);
                Assert.Equal("Session", scopeStarted.ScopeName);
            },
            serviceStarted =>
            {
                Assert.Equal(RuntimeLoadingOperationStage.ScopeInitializing, serviceStarted.Stage);
                Assert.Equal(RuntimeLoadingOperationState.Running, serviceStarted.State);
                Assert.Equal(0d, serviceStarted.Percent);
                Assert.Equal(0, serviceStarted.CurrentStep);
                Assert.Equal(2, serviceStarted.TotalSteps);
                Assert.Contains("ServiceA", serviceStarted.Message, StringComparison.Ordinal);
            },
            serviceCompleted =>
            {
                Assert.Equal(RuntimeLoadingOperationStage.ScopeInitializing, serviceCompleted.Stage);
                Assert.Equal(RuntimeLoadingOperationState.Running, serviceCompleted.State);
                Assert.Equal(50d, serviceCompleted.Percent);
                Assert.Equal(1, serviceCompleted.CurrentStep);
                Assert.Equal(2, serviceCompleted.TotalSteps);
                Assert.Contains("ServiceA", serviceCompleted.Message, StringComparison.Ordinal);
            },
            scopeCompleted =>
            {
                Assert.Equal(RuntimeLoadingOperationStage.Completed, scopeCompleted.Stage);
                Assert.Equal(RuntimeLoadingOperationState.Completed, scopeCompleted.State);
                Assert.Equal(100d, scopeCompleted.Percent);
                Assert.Equal(2, scopeCompleted.CurrentStep);
                Assert.Equal(2, scopeCompleted.TotalSteps);
                Assert.Equal("op-initialize", scopeCompleted.OperationId);
                Assert.Equal(RuntimeLoadingOperationKind.Initialize, scopeCompleted.OperationKind);
            });
    }

    [Fact]
    public void Adapter_EmitsMonotonicPercentAcrossScopeLifecycle()
    {
        var observer = new CollectingRuntimeLoadingProgressObserver();
        var adapter = new RuntimeLoadingProgressNotifierAdapter(observer, operationId: "op-monotonic");

        adapter.OnScopeStarted(GameContextType.Module, totalServices: 3);
        adapter.OnServiceStarted(GameContextType.Module, typeof(ServiceA), completedServices: 0, totalServices: 3);
        adapter.OnServiceCompleted(GameContextType.Module, typeof(ServiceA), completedServices: 1, totalServices: 3);
        adapter.OnServiceStarted(GameContextType.Module, typeof(ServiceB), completedServices: 1, totalServices: 3);
        adapter.OnServiceCompleted(GameContextType.Module, typeof(ServiceB), completedServices: 2, totalServices: 3);
        adapter.OnServiceStarted(GameContextType.Module, typeof(ServiceC), completedServices: 2, totalServices: 3);
        adapter.OnServiceCompleted(GameContextType.Module, typeof(ServiceC), completedServices: 3, totalServices: 3);
        adapter.OnScopeCompleted(GameContextType.Module, totalServices: 3);

        var percents = observer.Snapshots.Select(snapshot => snapshot.Percent).ToArray();
        for (var index = 1; index < percents.Length; index++)
        {
            Assert.True(
                percents[index] >= percents[index - 1],
                $"Percent at {index} ({percents[index]}) is less than previous percent ({percents[index - 1]}).");
        }

        Assert.Equal(100d, percents[percents.Length - 1]);
    }

    [Fact]
    public void Adapter_AllowsNullObserver()
    {
        var adapter = new RuntimeLoadingProgressNotifierAdapter(observer: null);

        adapter.OnScopeStarted(GameContextType.Global, totalServices: 0);
        adapter.OnScopeCompleted(GameContextType.Global, totalServices: 0);
    }

    [Fact]
    public void Adapter_SplitOperationPerScope_AssignsDeterministicOperationIds()
    {
        var observer = new CollectingRuntimeLoadingProgressObserver();
        var adapter = new RuntimeLoadingProgressNotifierAdapter(
            observer,
            operationId: "op-split",
            splitOperationPerScope: true);

        adapter.OnScopeStarted(GameContextType.Global, totalServices: 0);
        adapter.OnScopeCompleted(GameContextType.Global, totalServices: 0);
        adapter.OnScopeStarted(GameContextType.Session, totalServices: 0);
        adapter.OnScopeCompleted(GameContextType.Session, totalServices: 0);

        var operationIds = observer.Snapshots
            .Select(snapshot => snapshot.OperationId)
            .Distinct()
            .ToArray();

        Assert.Equal(new[] { "op-split", "op-split-s02" }, operationIds);
    }

    [Fact]
    public void Adapter_SplitOperationPerScope_SecondaryScopeStartsFromPreparingStage()
    {
        var observer = new CollectingRuntimeLoadingProgressObserver();
        var adapter = new RuntimeLoadingProgressNotifierAdapter(
            observer,
            operationId: "op-split",
            splitOperationPerScope: true);

        adapter.OnScopeStarted(GameContextType.Global, totalServices: 0);
        adapter.OnScopeCompleted(GameContextType.Global, totalServices: 0);
        adapter.OnScopeStarted(GameContextType.Session, totalServices: 0);
        adapter.OnScopeCompleted(GameContextType.Session, totalServices: 0);

        var secondaryScopeSnapshots = observer.Snapshots
            .Where(snapshot => snapshot.OperationId == "op-split-s02")
            .ToArray();

        Assert.Collection(
            secondaryScopeSnapshots,
            preparing =>
            {
                Assert.Equal(RuntimeLoadingOperationStage.Preparing, preparing.Stage);
                Assert.Equal(RuntimeLoadingOperationState.Running, preparing.State);
                Assert.Equal(0d, preparing.Percent);
            },
            scopeStarted =>
            {
                Assert.Equal(RuntimeLoadingOperationStage.ScopeInitializing, scopeStarted.Stage);
                Assert.Equal(RuntimeLoadingOperationState.Running, scopeStarted.State);
            },
            scopeCompleted =>
            {
                Assert.Equal(RuntimeLoadingOperationStage.Completed, scopeCompleted.Stage);
                Assert.Equal(RuntimeLoadingOperationState.Completed, scopeCompleted.State);
                Assert.Equal(100d, scopeCompleted.Percent);
            });
    }

    private sealed class CollectingRuntimeLoadingProgressObserver : IRuntimeLoadingProgressObserver
    {
        public List<RuntimeLoadingOperationSnapshot> Snapshots { get; } = new();

        public void OnLoadingProgress(RuntimeLoadingOperationSnapshot snapshot)
        {
            Snapshots.Add(snapshot);
        }
    }

    private sealed class ServiceA;
    private sealed class ServiceB;
    private sealed class ServiceC;
}
