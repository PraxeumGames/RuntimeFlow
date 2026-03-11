using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

internal interface ITestSceneService : ISceneInitializableService
{
    int Attempts { get; }
}

internal sealed class AttemptControlledSceneService : ITestSceneService
{
    private readonly Func<int, CancellationToken, Task> _behavior;
    private int _attempts;

    public AttemptControlledSceneService(Func<int, CancellationToken, Task> behavior)
    {
        _behavior = behavior ?? throw new ArgumentNullException(nameof(behavior));
    }

    public int Attempts => Volatile.Read(ref _attempts);

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _attempts);
        return _behavior(attempt, cancellationToken);
    }
}
