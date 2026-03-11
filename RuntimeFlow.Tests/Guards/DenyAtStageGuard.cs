using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

internal sealed class DenyAtStageGuard : IRuntimeFlowGuard
{
    private readonly RuntimeFlowGuardStage _stage;
    private readonly string _reasonCode;
    private readonly string _reason;

    public DenyAtStageGuard(RuntimeFlowGuardStage stage, string reasonCode, string reason)
    {
        _stage = stage;
        _reasonCode = reasonCode;
        _reason = reason;
    }

    public Task<RuntimeFlowGuardResult> EvaluateAsync(
        RuntimeFlowGuardContext context,
        CancellationToken cancellationToken = default)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));

        var result = context.Stage == _stage
            ? RuntimeFlowGuardResult.Deny(_reasonCode, _reason)
            : RuntimeFlowGuardResult.Allow();
        return Task.FromResult(result);
    }
}
