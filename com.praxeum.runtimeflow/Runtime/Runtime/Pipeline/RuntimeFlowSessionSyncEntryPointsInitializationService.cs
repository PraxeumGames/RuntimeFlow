using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public sealed class RuntimeFlowSessionSyncEntryPointsInitializationService :
        RuntimeFlowVContainerEntryPointsInitializationService,
        ISessionInitializableService,
        IRuntimeFlowSessionSyncEntryPointsBootstrapService
    {
        private static readonly ILogger LoggerInstance = NullLogger.Instance;

        public RuntimeFlowSessionSyncEntryPointsInitializationService(
            IObjectResolver resolver,
            RuntimeFlowVContainerEntryPointsSettings? settings = null)
            : base(resolver, "session", LoggerInstance, settings)
        {
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return InitializeVContainerEntryPointsAsync(cancellationToken);
        }

        protected override void InitializeInitializables(
            IObjectResolver resolver,
            IReadOnlyList<Registration> initializableRegistrations,
            CancellationToken cancellationToken)
        {
            RuntimeFlowVContainerInitializableRunner.InitializeSessionByStages(
                resolver,
                initializableRegistrations,
                ScopeName,
                Logger,
                cancellationToken);
        }
    }
}
