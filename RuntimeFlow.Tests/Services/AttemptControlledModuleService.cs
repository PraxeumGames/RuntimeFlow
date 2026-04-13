using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

internal interface ITestModuleService : IModuleInitializableService
{
    int Attempts { get; }
}

internal sealed class AttemptControlledModuleService : AttemptControlledInitializableServiceBase, ITestModuleService
{
    public AttemptControlledModuleService(Func<int, CancellationToken, Task> behavior)
        : base(behavior)
    {
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        return ExecuteAttemptAsync(cancellationToken);
    }
}
