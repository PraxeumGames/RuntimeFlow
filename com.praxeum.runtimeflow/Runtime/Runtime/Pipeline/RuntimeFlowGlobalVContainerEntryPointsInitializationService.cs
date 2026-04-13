using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Contexts
{
    public sealed class RuntimeFlowGlobalVContainerEntryPointsInitializationService :
        RuntimeFlowVContainerEntryPointsInitializationService,
        IGlobalInitializableService
    {
        private static readonly ILogger LoggerInstance = NullLogger.Instance;

        public RuntimeFlowGlobalVContainerEntryPointsInitializationService(
            IObjectResolver resolver,
            RuntimeFlowVContainerEntryPointsSettings? settings = null)
            : base(resolver, "global", LoggerInstance, settings)
        {
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return InitializeVContainerEntryPointsAsync(cancellationToken);
        }

        protected override IObjectResolver GetEntryPointResolver(IObjectResolver resolver)
        {
            if (resolver is not IScopedObjectResolver scopedResolver)
            {
                return resolver;
            }

            var current = scopedResolver;
            while (current.Parent is IScopedObjectResolver parentScopedResolver)
            {
                current = parentScopedResolver;
            }

            return current.Parent ?? current;
        }
    }
}
