using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

internal interface ITestSceneService : ISceneInitializableService
{
    int Attempts { get; }
}

internal sealed class AttemptControlledSceneService : AttemptControlledInitializableServiceBase, ITestSceneService
{
    public AttemptControlledSceneService(Func<int, CancellationToken, Task> behavior)
        : base(behavior)
    {
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        return ExecuteAttemptAsync(cancellationToken);
    }
}
