using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

internal interface ITestSessionService : ISessionInitializableService
{
    int Attempts { get; }
}

internal sealed class AttemptControlledSessionService : AttemptControlledInitializableServiceBase, ITestSessionService
{
    public AttemptControlledSessionService(Func<int, CancellationToken, Task> behavior)
        : base(behavior)
    {
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        return ExecuteAttemptAsync(cancellationToken);
    }
}
