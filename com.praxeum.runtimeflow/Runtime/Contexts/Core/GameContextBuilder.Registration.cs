using System;
using System.Collections.Generic;
using System.Linq;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder
    {
        public IGameScopeRegistrationBuilder Global()
        {
            return CreateRootScopeRegistrationBuilder(GameContextType.Global, typeof(GlobalScope));
        }

        public IGameScopeRegistrationBuilder Session()
        {
            return CreateRootScopeRegistrationBuilder(GameContextType.Session, typeof(SessionScope));
        }

        public IGameContextBuilder DefineGlobalScope()
        {
            return EnsureRootScopeDefined(GameContextType.Global, typeof(GlobalScope));
        }

        public IGameContextBuilder DefineSessionScope()
        {
            return EnsureRootScopeDefined(GameContextType.Session, typeof(SessionScope));
        }

        public IGameContextBuilder Scene<TScope>() where TScope : ISceneScope, new()
        {
            return Scene(new TScope());
        }

        public IGameContextBuilder Scene<TScope>(TScope installer) where TScope : ISceneScope
        {
            if (installer == null) throw new ArgumentNullException(nameof(installer));
            DefineScope(typeof(TScope), GameContextType.Scene);
            var regBuilder = CreateScopeRegistrationBuilder(typeof(TScope));
            installer.Configure(regBuilder);
            return this;
        }

        public IGameContextBuilder Module<TScope>() where TScope : IModuleScope, new()
        {
            return Module(new TScope());
        }

        public IGameContextBuilder Module<TScope>(TScope installer) where TScope : IModuleScope
        {
            if (installer == null) throw new ArgumentNullException(nameof(installer));
            DefineScope(typeof(TScope), GameContextType.Module);
            var regBuilder = CreateScopeRegistrationBuilder(typeof(TScope));
            installer.Configure(regBuilder);
            return this;
        }

        public bool TryResolveScopeType(Type scopeType, out GameContextType scope)
        {
            if (scopeType == null) throw new ArgumentNullException(nameof(scopeType));
            return _scopeRegistry.TryResolveScopeType(scopeType, out scope);
        }

        public ScopeLifecycleState GetScopeLifecycleState(Type scopeType)
        {
            return GetScopeState(scopeType);
        }

        internal void BindScopedRegistration(
            GameContextType scope,
            Type? scopeKey,
            Action<IGameContext> registration)
        {
            _scopeProfiles.BindScopedRegistration(scope, scopeKey, registration);
        }

        internal void DeferScopedRegistration(
            GameContextType scope,
            Type? scopeKey,
            Action<IGameContext> registration)
        {
            _deferredRegistrations.DeferScopedRegistration(scope, scopeKey, registration);
        }

        internal void DeferDecoration(
            GameContextType scope,
            Type? scopeKey,
            Type serviceType,
            Type decoratorType)
        {
            _deferredRegistrations.DeferDecoration(scope, scopeKey, serviceType, decoratorType);
        }

        internal void FlushDeferredScopedRegistrations()
        {
            _deferredRegistrations.Flush(BindScopedRegistration);
        }

        private IGameContextBuilder DefineScope(Type scopeType, GameContextType scope)
        {
            _scopeRegistry.DeclareScope(scopeType, scope);
            return this;
        }

        private IGameScopeRegistrationBuilder CreateScopeRegistrationBuilder(Type scopeType)
        {
            if (scopeType == null) throw new ArgumentNullException(nameof(scopeType));
            if (!_scopeRegistry.TryGetDeclaredScope(scopeType, out var scope))
            {
                throw new ScopeNotDeclaredException(scopeType);
            }

            return new ScopedRegistrationBuilder(this, scope, scopeType);
        }

        private IGameContextBuilder EnsureRootScopeDefined(GameContextType scope, Type builtInScopeType)
        {
            if (scope != GameContextType.Global && scope != GameContextType.Session)
                throw new ArgumentOutOfRangeException(nameof(scope), scope, "Built-in root scope helpers only support Global and Session.");

            if (_scopeRegistry.FindDeclaredScopeKey(scope) != null)
                return this;

            return DefineScope(builtInScopeType, scope);
        }

        private IGameScopeRegistrationBuilder CreateRootScopeRegistrationBuilder(GameContextType scope, Type builtInScopeType)
        {
            if (scope != GameContextType.Global && scope != GameContextType.Session)
                throw new ArgumentOutOfRangeException(nameof(scope), scope, "Built-in root scope helpers only support Global and Session.");

            var scopeType = _scopeRegistry.FindDeclaredScopeKey(scope);
            if (scopeType == null)
            {
                DefineScope(builtInScopeType, scope);
                scopeType = builtInScopeType;
            }

            return CreateScopeRegistrationBuilder(scopeType);
        }

        private static Type? ResolveScopeKey(GameContextType scope, Type scopeType)
        {
            return scope switch
            {
                GameContextType.Global => null,
                GameContextType.Session => null,
                GameContextType.Scene => scopeType,
                GameContextType.Module => scopeType,
                _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported scope.")
            };
        }

        private static Type[] BuildImportedServiceTypes(Type resolvedType, object instance, Type[] additionalServiceTypes)
        {
            if (resolvedType == null) throw new ArgumentNullException(nameof(resolvedType));
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (additionalServiceTypes == null) throw new ArgumentNullException(nameof(additionalServiceTypes));
            if (instance is IObjectResolver)
            {
                throw new InvalidOperationException(
                    "Importing IObjectResolver into a RuntimeFlow scope is not supported because it breaks the scope chain.");
            }

            var implementationType = instance.GetType();
            var exposedTypes = new List<Type> { implementationType };

            if (resolvedType != implementationType)
                exposedTypes.Add(resolvedType);

            foreach (var serviceType in additionalServiceTypes)
            {
                if (serviceType == null)
                    throw new ArgumentException("Additional service types cannot contain null.", nameof(additionalServiceTypes));

                if (!serviceType.IsInstanceOfType(instance))
                {
                    var implementationTypeName = implementationType.FullName ?? implementationType.Name;
                    var serviceTypeName = serviceType.FullName ?? serviceType.Name;
                    throw new ArgumentException(
                        $"Resolved instance type '{implementationTypeName}' cannot be exposed as '{serviceTypeName}'.",
                        nameof(additionalServiceTypes));
                }

                exposedTypes.Add(serviceType);
            }

            return exposedTypes.Distinct().ToArray();
        }

        private sealed class ScopedRegistrationBuilder : IGameScopeRegistrationBuilder
        {
            private readonly GameContextBuilder _owner;
            private readonly GameContextType _scope;
            private readonly Type _scopeType;
            private Type? _pendingImplementationType;
            private Lifetime _pendingLifetime;
            private object? _pendingInstance;
            private bool _hasPendingInstance;

            public ScopedRegistrationBuilder(GameContextBuilder owner, GameContextType scope, Type scopeType)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _scope = scope;
                _scopeType = scopeType ?? throw new ArgumentNullException(nameof(scopeType));
            }

            public IGameScopeRegistrationBuilder Register<TInterface, TImplementation>(Lifetime lifetime)
                where TImplementation : class, TInterface
            {
                FlushPending();
                var serviceType = typeof(TInterface);
                var implType = typeof(TImplementation);
                _owner.DeferScopedRegistration(_scope, ResolveScopeKey(_scope, _scopeType),
                    context => context.Register(serviceType, implType, lifetime));
                return this;
            }

            public IGameScopeRegistrationBuilder Register<TImplementation>(Lifetime lifetime)
                where TImplementation : class
            {
                FlushPending();
                _pendingImplementationType = typeof(TImplementation);
                _pendingLifetime = lifetime;
                _hasPendingInstance = false;
                return this;
            }

            public IGameScopeRegistrationBuilder Register(Type implementationType, Lifetime lifetime)
            {
                FlushPending();
                if (implementationType == null) throw new ArgumentNullException(nameof(implementationType));
                if (implementationType.IsGenericTypeDefinition)
                    throw new InvalidOperationException(
                        $"[RFRC2003] Cannot register open generic type '{implementationType.FullName ?? implementationType.Name}'. " +
                        "Use a closed generic type or register via ConfigureContainer instead.");
                _pendingImplementationType = implementationType;
                _pendingLifetime = lifetime;
                _hasPendingInstance = false;
                return this;
            }

            public IGameScopeRegistrationBuilder As<TInterface>()
            {
                if (_pendingImplementationType == null && !_hasPendingInstance)
                    throw new InvalidOperationException("Call Register or RegisterInstance before As<T>().");
                if (_hasPendingInstance)
                {
                    var inst = _pendingInstance;
                    var serviceType = typeof(TInterface);
                    _owner.DeferScopedRegistration(_scope, ResolveScopeKey(_scope, _scopeType),
                        context => context.RegisterInstance(serviceType, inst!));
                }
                else
                {
                    var implType = _pendingImplementationType!;
                    var lifetime = _pendingLifetime;
                    var serviceType = typeof(TInterface);
                    _owner.DeferScopedRegistration(_scope, ResolveScopeKey(_scope, _scopeType),
                        context => context.Register(serviceType, implType, lifetime));
                }
                return this;
            }

            public IGameScopeRegistrationBuilder As(Type interfaceType)
            {
                if (_pendingImplementationType == null && !_hasPendingInstance)
                    throw new InvalidOperationException("Call Register or RegisterInstance before As(Type).");
                if (interfaceType == null) throw new ArgumentNullException(nameof(interfaceType));
                if (_hasPendingInstance)
                {
                    var inst = _pendingInstance;
                    _owner.DeferScopedRegistration(_scope, ResolveScopeKey(_scope, _scopeType),
                        context => context.RegisterInstance(interfaceType, inst!));
                }
                else
                {
                    var implType = _pendingImplementationType!;
                    var lifetime = _pendingLifetime;
                    _owner.DeferScopedRegistration(_scope, ResolveScopeKey(_scope, _scopeType),
                        context => context.Register(interfaceType, implType, lifetime));
                }
                return this;
            }

            public IGameScopeRegistrationBuilder AsSelf()
            {
                if (_pendingImplementationType == null && !_hasPendingInstance)
                    throw new InvalidOperationException("Call Register or RegisterInstance before AsSelf().");
                if (_hasPendingInstance)
                {
                    var inst = _pendingInstance;
                    var implType = inst!.GetType();
                    _owner.DeferScopedRegistration(_scope, ResolveScopeKey(_scope, _scopeType),
                        context => context.RegisterInstance(implType, inst));
                }
                else
                {
                    var implType = _pendingImplementationType!;
                    var lifetime = _pendingLifetime;
                    _owner.DeferScopedRegistration(_scope, ResolveScopeKey(_scope, _scopeType),
                        context => context.Register(implType, implType, lifetime));
                }
                return this;
            }

            public IGameScopeRegistrationBuilder RegisterInstance<TInterface>(TInterface instance)
            {
                if (instance is null) throw new ArgumentNullException(nameof(instance));
                FlushPending();
                _pendingInstance = instance;
                _pendingImplementationType = null;
                _hasPendingInstance = true;
                _owner.DeferScopedRegistration(_scope, ResolveScopeKey(_scope, _scopeType),
                    context => context.RegisterInstance<TInterface>(instance));
                return this;
            }

            public IGameScopeRegistrationBuilder Import<TImplementation>(IObjectResolver resolver, params Type[] additionalServiceTypes)
            {
                if (resolver == null) throw new ArgumentNullException(nameof(resolver));
                if (additionalServiceTypes == null) throw new ArgumentNullException(nameof(additionalServiceTypes));
                FlushPending();

                _owner.DeferScopedRegistration(_scope, ResolveScopeKey(_scope, _scopeType), context =>
                {
                    var instance = resolver.Resolve<TImplementation>();
                    if (instance == null)
                        throw new InvalidOperationException($"Resolver returned null for '{typeof(TImplementation).FullName ?? typeof(TImplementation).Name}'.");

                    var serviceTypes = BuildImportedServiceTypes(typeof(TImplementation), instance, additionalServiceTypes);
                    if (context is GameContext gameContext)
                    {
                        gameContext.RegisterImportedInstance(instance, serviceTypes);
                    }
                    else
                    {
                        context.RegisterInstance(instance, serviceTypes);
                    }
                });
                return this;
            }

            public IGameScopeRegistrationBuilder Decorate<TService, TDecorator>() where TDecorator : class, TService
            {
                FlushPending();
                _owner.DeferDecoration(_scope, ResolveScopeKey(_scope, _scopeType), typeof(TService), typeof(TDecorator));
                return this;
            }

            public IGameScopeRegistrationBuilder ConfigureContainer(Action<VContainer.IContainerBuilder> configure)
            {
                if (configure == null) throw new ArgumentNullException(nameof(configure));
                FlushPending();
                _owner.DeferScopedRegistration(_scope, ResolveScopeKey(_scope, _scopeType),
                    context => context.ConfigureContainer(configure));
                return this;
            }

            private void FlushPending()
            {
                _hasPendingInstance = false;
                _pendingInstance = null;
                if (_pendingImplementationType == null) return;
                var implType = _pendingImplementationType;
                var lifetime = _pendingLifetime;
                _owner.DeferScopedRegistration(_scope, ResolveScopeKey(_scope, _scopeType),
                    context => context.Register(implType, implType, lifetime));
                _pendingImplementationType = null;
            }
        }
    }
}
