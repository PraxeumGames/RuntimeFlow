using System;
using System.Collections.Generic;
using RuntimeFlow.Contexts;
using Xunit;

namespace RuntimeFlow.Tests;

internal static class RuntimeLoadingProgressAssertions
{
    public static void AssertMonotonicPercent(IReadOnlyList<RuntimeLoadingOperationSnapshot> snapshots)
    {
        Assert.NotEmpty(snapshots);
        for (var index = 1; index < snapshots.Count; index++)
        {
            Assert.True(
                snapshots[index].Percent >= snapshots[index - 1].Percent,
                $"Percent at {index} ({snapshots[index].Percent}) is less than previous percent ({snapshots[index - 1].Percent}).");
        }
    }

    public static void AssertMonotonicStageAndPercent(IReadOnlyList<RuntimeLoadingOperationSnapshot> snapshots)
    {
        AssertMonotonicPercent(snapshots);
        for (var index = 1; index < snapshots.Count; index++)
        {
            Assert.True((int)snapshots[index].Stage >= (int)snapshots[index - 1].Stage);
        }
    }

    public static void AssertProgression(
        IReadOnlyList<RuntimeLoadingOperationSnapshot> snapshots,
        RuntimeLoadingOperationStage expectedFirstStage = RuntimeLoadingOperationStage.Preparing)
    {
        Assert.NotEmpty(snapshots);
        Assert.Equal(expectedFirstStage, snapshots[0].Stage);

        var completedSnapshot = snapshots[^1];
        Assert.Equal(RuntimeLoadingOperationStage.Completed, completedSnapshot.Stage);
        Assert.Equal(RuntimeLoadingOperationState.Completed, completedSnapshot.State);
        Assert.Equal(100d, completedSnapshot.Percent);

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

        Assert.StartsWith(expectedPrefix, operationId, StringComparison.Ordinal);
    }
}
