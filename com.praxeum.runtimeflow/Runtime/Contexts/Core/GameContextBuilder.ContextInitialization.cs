using System;
using System.Threading;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder
    {
        private static bool IsStaleGenerationCancellation(Exception exception, CancellationToken cancellationToken)
            => exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;
    }
}
