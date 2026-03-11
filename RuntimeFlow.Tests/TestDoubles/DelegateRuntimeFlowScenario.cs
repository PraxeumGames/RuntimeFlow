using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

internal sealed class DelegateRuntimeFlowScenario : IRuntimeFlowScenario
{
    private readonly Func<IRuntimeFlowContext, CancellationToken, Task> _execute;

    public DelegateRuntimeFlowScenario(Func<IRuntimeFlowContext, CancellationToken, Task> execute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public Task ExecuteAsync(IRuntimeFlowContext context, CancellationToken cancellationToken = default)
    {
        return _execute(context, cancellationToken);
    }
}
