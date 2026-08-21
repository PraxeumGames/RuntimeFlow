using System;
using System.Collections.Generic;
using System.Threading;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder
    {
        private (HashSet<Type> InitializedServices, Dictionary<Type, object> AvailableServices) CreateSeededInitializationState(
            params IGameContext?[] contexts)
        {
            var initializedServices = new HashSet<Type>();
            var availableServices = new Dictionary<Type, object>();
            foreach (var context in contexts)
                SeedInitializedFromContext(context, initializedServices, availableServices);
            return (initializedServices, availableServices);
        }

        private static bool IsStaleGenerationCancellation(Exception exception, CancellationToken cancellationToken)
            => exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

        private void SeedInitializedFromContext(
            IGameContext? context,
            ISet<Type> initializedServices,
            IDictionary<Type, object> availableServices)
        {
            if (context is not GameContext gameContext)
                return;
            foreach (var initializer in gameContext.InitializationOrder)
            {
                initializedServices.Add(initializer.ServiceType);
                availableServices[initializer.ServiceType] = gameContext.Resolve(initializer);
            }
        }
    }
}
