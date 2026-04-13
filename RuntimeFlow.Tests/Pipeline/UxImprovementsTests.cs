using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

public sealed partial class UxImprovementsTests
{
    private sealed class UndeclaredScope { }

    private sealed class FailingSessionService : ITestSessionService
    {
        public int Attempts => 0;

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Simulated init failure");
        }
    }
}
