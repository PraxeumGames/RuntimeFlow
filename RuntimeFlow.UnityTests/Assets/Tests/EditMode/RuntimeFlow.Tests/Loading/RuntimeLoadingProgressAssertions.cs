using NUnit.Framework;
using System;
using System.Collections.Generic;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{

internal static class RuntimeLoadingProgressAssertions
{
    public static void AssertMonotonicPercent(IReadOnlyList<RuntimeLoadingOperationSnapshot> snapshots)
    {
        Assert.That(snapshots, Is.Not.Empty);
        for (var index = 1; index < snapshots.Count; index++)
        {
            Assert.That(
                snapshots[index].Percent,
                Is.GreaterThanOrEqualTo(snapshots[index - 1].Percent),
                $"Percent at {index} ({snapshots[index].Percent}) is less than previous percent ({snapshots[index - 1].Percent}).");
        }
    }

    public static void AssertMonotonicStageAndPercent(IReadOnlyList<RuntimeLoadingOperationSnapshot> snapshots)
    {
        AssertMonotonicPercent(snapshots);
        for (var index = 1; index < snapshots.Count; index++)
        {
            Assert.That((int)snapshots[index].Stage, Is.GreaterThanOrEqualTo((int)snapshots[index - 1].Stage));
        }
    }

    public static void AssertProgression(
        IReadOnlyList<RuntimeLoadingOperationSnapshot> snapshots,
        RuntimeLoadingOperationStage expectedFirstStage = RuntimeLoadingOperationStage.Preparing)
    {
        Assert.That(snapshots, Is.Not.Empty);
        Assert.That(snapshots[0].Stage, Is.EqualTo(expectedFirstStage));

        var completedSnapshot = snapshots[^1];
        Assert.That(completedSnapshot.Stage, Is.EqualTo(RuntimeLoadingOperationStage.Completed));
        Assert.That(completedSnapshot.State, Is.EqualTo(RuntimeLoadingOperationState.Completed));
        Assert.That(completedSnapshot.Percent, Is.EqualTo(100d));

        AssertMonotonicStageAndPercent(snapshots);
    }

    public static void AssertOperationIdPrefix(
        RuntimeLoadingOperationKind operationKind,
        string operationId)
    {
        var expectedPrefix = operationKind switch
        {
            RuntimeLoadingOperationKind.RestartSession => "restart_session-",
            RuntimeLoadingOperationKind.ReloadScene => "reload_scene-",
            RuntimeLoadingOperationKind.ReloadModule => "reload_module-",
            RuntimeLoadingOperationKind.LoadModule => "load_module-",
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, "Unsupported operation kind.")
        };

        Assert.That(operationId, Does.StartWith(expectedPrefix));
    }
}
}
