using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Tests;

internal abstract class AttemptControlledInitializableServiceBase
{
    private readonly Func<int, CancellationToken, Task> _behavior;
    private int _attempts;

    protected AttemptControlledInitializableServiceBase(Func<int, CancellationToken, Task> behavior)
    {
        _behavior = behavior ?? throw new ArgumentNullException(nameof(behavior));
    }

    public int Attempts => Volatile.Read(ref _attempts);

    protected Task ExecuteAttemptAsync(CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _attempts);
        return _behavior(attempt, cancellationToken);
    }
}
