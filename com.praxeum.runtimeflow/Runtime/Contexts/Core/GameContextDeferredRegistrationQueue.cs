using System;
using System.Collections.Generic;
using VContainer;

namespace RuntimeFlow.Contexts
{
    internal sealed class GameContextDeferredRegistrationQueue
    {
        private readonly List<DeferredScopedRegistration> _deferredScopedRegistrations = new();
        private readonly List<DeferredDecoration> _deferredDecorations = new();

        public void DeferScopedRegistration(
            GameContextType scope,
            Type? scopeKey,
            Action<IGameContext> registration)
        {
            if (registration == null) throw new ArgumentNullException(nameof(registration));
            _deferredScopedRegistrations.Add(new DeferredScopedRegistration(scope, scopeKey, registration));
        }

        public void DeferDecoration(
            GameContextType scope,
            Type? scopeKey,
            Type serviceType,
            Type decoratorType)
        {
            _deferredDecorations.Add(new DeferredDecoration(scope, scopeKey, serviceType, decoratorType));
        }

        public void Flush(Action<GameContextType, Type?, Action<IGameContext>> bindScopedRegistration)
        {
            if (bindScopedRegistration == null) throw new ArgumentNullException(nameof(bindScopedRegistration));
            if (_deferredScopedRegistrations.Count == 0 && _deferredDecorations.Count == 0)
                return;

            foreach (var deferred in _deferredScopedRegistrations)
            {
                bindScopedRegistration(deferred.Scope, deferred.ScopeKey, deferred.Registration);
            }

            _deferredScopedRegistrations.Clear();

            foreach (var decoration in _deferredDecorations)
            {
                var serviceType = decoration.ServiceType;
                var decoratorType = decoration.DecoratorType;
                bindScopedRegistration(decoration.Scope, decoration.ScopeKey, context =>
                {
                    if (context is GameContext gameContext)
                        gameContext.Decorate(serviceType, decoratorType);
                    else
                        context.ConfigureContainer(builder =>
                        {
                            builder.Register(decoratorType, Lifetime.Singleton).As(serviceType);
                        });
                });
            }

            _deferredDecorations.Clear();
        }
    }
}
