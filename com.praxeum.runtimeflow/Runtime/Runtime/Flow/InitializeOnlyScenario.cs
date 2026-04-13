using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    internal sealed class InitializeOnlyScenario : IRuntimeFlowScenario
    {
        internal static readonly InitializeOnlyScenario Instance = new();

        private InitializeOnlyScenario()
        {
        }

        public Task ExecuteAsync(IRuntimeFlowContext context, CancellationToken cancellationToken = default)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return context.InitializeAsync(cancellationToken);
        }
    }
}
