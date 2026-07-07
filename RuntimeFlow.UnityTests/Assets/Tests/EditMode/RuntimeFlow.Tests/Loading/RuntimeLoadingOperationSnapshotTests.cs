using NUnit.Framework;
using System;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{

public sealed class RuntimeLoadingOperationSnapshotTests
{
    [TestCase(-0.1)]
    [TestCase(100.1)]
    public void Constructor_InvalidPercent_Throws(double percent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSnapshot(percent: percent));
    }

    [TestCase(-1, 1)]
    [TestCase(1, -1)]
    [TestCase(2, 1)]
    public void Constructor_InvalidStepBounds_Throws(int currentStep, int totalSteps)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateSnapshot(currentStep: currentStep, totalSteps: totalSteps));
    }

    private static RuntimeLoadingOperationSnapshot CreateSnapshot(
        double percent = 0d,
        int currentStep = 0,
        int totalSteps = 0)
    {
        return new RuntimeLoadingOperationSnapshot(
            operationId: "op-1",
            operationKind: RuntimeLoadingOperationKind.LoadModule,
            stage: RuntimeLoadingOperationStage.ScopeInitializing,
            state: RuntimeLoadingOperationState.Running,
            scopeKey: typeof(RuntimeLoadingOperationSnapshotTests),
            scopeName: "TestModule",
            percent: percent,
            currentStep: currentStep,
            totalSteps: totalSteps,
            message: "loading",
            timestampUtc: DateTimeOffset.UtcNow);
    }
}
}
