using NUnit.Framework;
using System;
using System.Linq;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{

public sealed class RuntimeLoadingProgressNotifierAdapterTests
{
    [Test]
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

        var snapshots = observer.Snapshots.ToArray();
        Assert.That(snapshots, Has.Length.EqualTo(4));

        var scopeStarted = snapshots[0];
        Assert.That(scopeStarted.Stage, Is.EqualTo(RuntimeLoadingOperationStage.ScopeInitializing));
        Assert.That(scopeStarted.State, Is.EqualTo(RuntimeLoadingOperationState.Running));
        Assert.That(scopeStarted.Percent, Is.EqualTo(0d));
        Assert.That(scopeStarted.CurrentStep, Is.EqualTo(0));
        Assert.That(scopeStarted.TotalSteps, Is.EqualTo(2));
        Assert.That(scopeStarted.ScopeName, Is.EqualTo("Session"));

        var serviceStarted = snapshots[1];
        Assert.That(serviceStarted.Stage, Is.EqualTo(RuntimeLoadingOperationStage.ScopeInitializing));
        Assert.That(serviceStarted.State, Is.EqualTo(RuntimeLoadingOperationState.Running));
        Assert.That(serviceStarted.Percent, Is.EqualTo(0d));
        Assert.That(serviceStarted.CurrentStep, Is.EqualTo(0));
        Assert.That(serviceStarted.TotalSteps, Is.EqualTo(2));
        Assert.That(serviceStarted.Message?.Contains("ServiceA", StringComparison.Ordinal), Is.True);

        var serviceCompleted = snapshots[2];
        Assert.That(serviceCompleted.Stage, Is.EqualTo(RuntimeLoadingOperationStage.ScopeInitializing));
        Assert.That(serviceCompleted.State, Is.EqualTo(RuntimeLoadingOperationState.Running));
        Assert.That(serviceCompleted.Percent, Is.EqualTo(50d));
        Assert.That(serviceCompleted.CurrentStep, Is.EqualTo(1));
        Assert.That(serviceCompleted.TotalSteps, Is.EqualTo(2));
        Assert.That(serviceCompleted.Message?.Contains("ServiceA", StringComparison.Ordinal), Is.True);

        var scopeCompleted = snapshots[3];
        Assert.That(scopeCompleted.Stage, Is.EqualTo(RuntimeLoadingOperationStage.Completed));
        Assert.That(scopeCompleted.State, Is.EqualTo(RuntimeLoadingOperationState.Completed));
        Assert.That(scopeCompleted.Percent, Is.EqualTo(100d));
        Assert.That(scopeCompleted.CurrentStep, Is.EqualTo(2));
        Assert.That(scopeCompleted.TotalSteps, Is.EqualTo(2));
        Assert.That(scopeCompleted.OperationId, Is.EqualTo("op-initialize"));
        Assert.That(scopeCompleted.OperationKind, Is.EqualTo(RuntimeLoadingOperationKind.Initialize));
    }

    [Test]
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
        RuntimeLoadingProgressAssertions.AssertMonotonicPercent(observer.Snapshots);
        Assert.That(percents[^1], Is.EqualTo(100d));
    }

    [Test]
    public void Adapter_AllowsNullObserver()
    {
        var adapter = new RuntimeLoadingProgressNotifierAdapter(observer: null);

        adapter.OnScopeStarted(GameContextType.Global, totalServices: 0);
        adapter.OnScopeCompleted(GameContextType.Global, totalServices: 0);
    }

    [Test]
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

        Assert.That(operationIds, Is.EqualTo(new[] { "op-split", "op-split-s02" }));
    }

    [Test]
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

        Assert.That(secondaryScopeSnapshots, Has.Length.EqualTo(3));

        var preparing = secondaryScopeSnapshots[0];
        Assert.That(preparing.Stage, Is.EqualTo(RuntimeLoadingOperationStage.Preparing));
        Assert.That(preparing.State, Is.EqualTo(RuntimeLoadingOperationState.Running));
        Assert.That(preparing.Percent, Is.EqualTo(0d));

        var scopeStarted = secondaryScopeSnapshots[1];
        Assert.That(scopeStarted.Stage, Is.EqualTo(RuntimeLoadingOperationStage.ScopeInitializing));
        Assert.That(scopeStarted.State, Is.EqualTo(RuntimeLoadingOperationState.Running));

        var scopeCompleted = secondaryScopeSnapshots[2];
        Assert.That(scopeCompleted.Stage, Is.EqualTo(RuntimeLoadingOperationStage.Completed));
        Assert.That(scopeCompleted.State, Is.EqualTo(RuntimeLoadingOperationState.Completed));
        Assert.That(scopeCompleted.Percent, Is.EqualTo(100d));
    }

    [Test]
    public void Adapter_StartupOperationSnapshots_KeepCurrentAndLastSeparate()
    {
        var observer = new CollectingRuntimeLoadingProgressObserver();
        var adapter = new RuntimeLoadingProgressNotifierAdapter(observer, operationId: "op-startup");

        adapter.OnStartupOperationStarted(
            GameContextType.Global,
            RuntimeStartupOperationPhases.GlobalBootstrapOperations,
            "Catalog",
            completedOperations: 1,
            totalOperations: 3,
            elapsed: TimeSpan.FromMilliseconds(10));
        adapter.OnStartupOperationStep(
            GameContextType.Global,
            RuntimeStartupOperationPhases.GlobalBootstrapOperations,
            "Catalog",
            "UpdateCatalogs",
            "catalog-a",
            completedOperations: 1,
            totalOperations: 3,
            elapsed: TimeSpan.FromMilliseconds(20));

        Assert.That(adapter.CurrentStartupOperation, Is.Not.Null);
        Assert.That(adapter.CurrentStartupOperation!.Step, Is.EqualTo("UpdateCatalogs"));
        Assert.That(adapter.CurrentStartupOperation.Detail, Is.EqualTo("catalog-a"));

        adapter.OnStartupOperationCompleted(
            GameContextType.Global,
            RuntimeStartupOperationPhases.GlobalBootstrapOperations,
            "Catalog",
            completedOperations: 2,
            totalOperations: 3,
            elapsed: TimeSpan.FromMilliseconds(30));

        Assert.That(adapter.CurrentStartupOperation, Is.Null);
        Assert.That(adapter.LastStartupOperation, Is.Not.Null);
        Assert.That(adapter.LastStartupOperation!.State, Is.EqualTo(RuntimeStartupOperationState.Completed));
        Assert.That(adapter.LastStartupOperation.Step, Is.EqualTo("UpdateCatalogs"));
        Assert.That(adapter.LastStartupOperation.Detail, Is.EqualTo("catalog-a"));

        adapter.OnServiceStarted(GameContextType.Session, typeof(ServiceA), completedServices: 2, totalServices: 3);

        Assert.That(adapter.CurrentStartupOperation, Is.Null);
        Assert.That(adapter.LastStartupOperation!.OperationName, Is.EqualTo("Catalog"));
    }

    [Test]
    public void Adapter_StartupOperationFailure_PreservesDetail()
    {
        var observer = new CollectingRuntimeLoadingProgressObserver();
        var adapter = new RuntimeLoadingProgressNotifierAdapter(observer, operationId: "op-startup");
        var exception = new InvalidOperationException("boom");

        adapter.OnStartupOperationFailed(
            GameContextType.Global,
            RuntimeStartupOperationPhases.GlobalBootstrapOperations,
            "Catalog",
            "UpdateCatalogs",
            "catalog-a",
            exception,
            completedOperations: 1,
            totalOperations: 3,
            elapsed: TimeSpan.FromMilliseconds(40));

        Assert.That(adapter.CurrentStartupOperation, Is.Null);
        Assert.That(adapter.LastStartupOperation, Is.Not.Null);
        Assert.That(adapter.LastStartupOperation!.State, Is.EqualTo(RuntimeStartupOperationState.Failed));
        Assert.That(adapter.LastStartupOperation.Step, Is.EqualTo("UpdateCatalogs"));
        Assert.That(adapter.LastStartupOperation.Detail, Is.EqualTo("catalog-a"));
        Assert.That(observer.Snapshots.Last().Message, Does.Contain("detail=catalog-a"));
    }

    private sealed class ServiceA
    {
    }

    private sealed class ServiceB
    {
    }

    private sealed class ServiceC
    {
    }
}
}
