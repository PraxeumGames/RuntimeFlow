using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Events;
using RuntimeFlow.Initialization.Graph;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public partial class GameContextBuilder : IGameContextBuilder
    {
        private readonly List<Action<IGameContext>> _globalRegistrations = new();
        private readonly List<Action<IGameContext>> _sessionRegistrations = new();
        private readonly Dictionary<Type, ScopeProfile> _sceneProfiles = new();
        private readonly Dictionary<Type, ScopeProfile> _moduleProfiles = new();
        private readonly Dictionary<Type, GameContextType> _declaredScopeTypes = new();
        private readonly List<DeferredScopedRegistration> _deferredScopedRegistrations = new();
        private readonly List<DeferredDecoration> _deferredDecorations = new();

        private readonly Dictionary<Type, ScopeLifecycleState> _scopeStates = new();
        private readonly object _scopeStateSync = new();
        private readonly IInitializationExecutionScheduler _executionScheduler;
        private readonly RuntimeHealthSupervisor _healthSupervisor;
        private readonly ILogger _logger;
        private readonly Dictionary<(GameContextType Scope, Type? ScopeKey), List<Type>> _scopeInitializationOrder = new();
        private readonly Dictionary<Type, GameContext> _preloadedContexts = new();
        private readonly Dictionary<Type, GameContext> _additiveModuleContexts = new();
        private static readonly Lazy<Type[]> ExplicitDependencyTypeCatalog = new(
            BuildExplicitDependencyTypeCatalog,
            LazyThreadSafetyMode.ExecutionAndPublication);

        private Action<IGameContext>? _onGlobalInitialized;
        private Action<IGameContext>? _onSessionInitialized;
        private Action<IGameContext>? _onSceneInitialized;
        private Action<IGameContext>? _onModuleInitialized;

        private IGameContext? _globalContext;
        private GameContext? _sessionContext;
        private GameContext? _sceneContext;
        private GameContext? _moduleContext;
        private Type? _activeSceneScopeKey;
        private Type? _activeModuleScopeKey;

        internal Type? ActiveSceneScopeKey => _activeSceneScopeKey;
        internal Type? ActiveModuleScopeKey => _activeModuleScopeKey;
        private bool _ownsGlobalContext = true;

        private ScopeEventBus? _globalEventBus;
        private ScopeEventBus? _sessionEventBus;
        private ScopeEventBus? _sceneEventBus;
        private ScopeEventBus? _moduleEventBus;

        private CancellationTokenSource? _activeLoadCts;
        private Task _activeLoadTask = Task.CompletedTask;
        private long _runGeneration;

        private readonly Dictionary<Type, (GameContext Context, GameContextType Scope, Type? ScopeKey)> _lazyServiceBindings = new();
        private readonly HashSet<Type> _initializedLazyServices = new();
        private readonly SemaphoreSlim _lazyInitLock = new(1, 1);

        public GameContextBuilder(IInitializationExecutionScheduler? executionScheduler = null)
            : this(executionScheduler, null, null)
        {
        }

        internal GameContextBuilder(
            IInitializationExecutionScheduler? executionScheduler,
            RuntimeHealthSupervisor? healthSupervisor,
            ILogger? logger = null)
        {
            _executionScheduler = executionScheduler ?? InlineInitializationExecutionScheduler.Instance;
            _healthSupervisor = healthSupervisor ?? RuntimeHealthSupervisor.Disabled;
            _logger = logger ?? NullLogger.Instance;
        }

        internal Task ExecuteOnMainThreadAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            return _executionScheduler.ExecuteAsync(
                InitializationThreadAffinity.MainThread,
                operation,
                cancellationToken);
        }

        private Task ExecuteStageCallbackOnMainThreadAsync(
            Func<CancellationToken, Task> callback,
            CancellationToken cancellationToken)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            return _executionScheduler.ExecuteAsync(
                InitializationThreadAffinity.MainThread,
                callback,
                cancellationToken);
        }

        private Task DrainMainThreadFrameAsync(CancellationToken cancellationToken)
        {
            return _executionScheduler.ExecuteAsync(
                InitializationThreadAffinity.MainThread,
                async token =>
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Yield();
                    token.ThrowIfCancellationRequested();
                    await Task.Yield();
                },
                cancellationToken);
        }

        internal void UseExternalGlobalContext(IGameContext globalContext)
        {
            _globalContext = globalContext ?? throw new ArgumentNullException(nameof(globalContext));
            _ownsGlobalContext = false;
        }

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
            return _declaredScopeTypes.TryGetValue(scopeType, out scope);
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
            if (registration == null) throw new ArgumentNullException(nameof(registration));

            switch (scope)
            {
                case GameContextType.Global:
                    _globalRegistrations.Add(registration);
                    break;
                case GameContextType.Session:
                    _sessionRegistrations.Add(registration);
                    break;
                case GameContextType.Scene:
                    if (scopeKey == null)
                        throw new ArgumentNullException(nameof(scopeKey), "Scene scope key is required. Use the typed ConfigureScene<TScope>() API.");
                    GetOrCreateProfile(_sceneProfiles, scopeKey).Registrations.Add(registration);
                    break;
                case GameContextType.Module:
                    if (scopeKey == null)
                        throw new ArgumentNullException(nameof(scopeKey), "Module scope key is required. Use the typed ConfigureModule<TScope>() API.");
                    GetOrCreateProfile(_moduleProfiles, scopeKey).Registrations.Add(registration);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported scope.");
            }
        }

        internal void DeferScopedRegistration(
            GameContextType scope,
            Type? scopeKey,
            Action<IGameContext> registration)
        {
            if (registration == null) throw new ArgumentNullException(nameof(registration));
            _deferredScopedRegistrations.Add(new DeferredScopedRegistration(scope, scopeKey, registration));
        }

        internal void DeferDecoration(
            GameContextType scope,
            Type? scopeKey,
            Type serviceType,
            Type decoratorType)
        {
            _deferredDecorations.Add(new DeferredDecoration(scope, scopeKey, serviceType, decoratorType));
        }

        internal void FlushDeferredScopedRegistrations()
        {
            if (_deferredScopedRegistrations.Count == 0 && _deferredDecorations.Count == 0)
                return;

            foreach (var deferred in _deferredScopedRegistrations)
            {
                BindScopedRegistration(deferred.Scope, deferred.ScopeKey, deferred.Registration);
            }

            _deferredScopedRegistrations.Clear();

            foreach (var decoration in _deferredDecorations)
            {
                var svcType = decoration.ServiceType;
                var decType = decoration.DecoratorType;
                BindScopedRegistration(decoration.Scope, decoration.ScopeKey, context =>
                {
                    if (context is GameContext gc)
                        gc.Decorate(svcType, decType);
                    else
                        context.ConfigureContainer(builder =>
                        {
                            builder.Register(decType, Lifetime.Singleton).As(svcType);
                        });
                });
            }

            _deferredDecorations.Clear();
        }

        private IGameContextBuilder DefineScope(Type scopeType, GameContextType scope)
        {
            if (scopeType == null) throw new ArgumentNullException(nameof(scopeType));

            if (_declaredScopeTypes.TryGetValue(scopeType, out var existingScope))
            {
                var scopeTypeName = scopeType.FullName ?? scopeType.Name;
                if (existingScope == scope)
                {
                    throw new ScopeRegistrationException("GBSR3001",
                        $"GBSR3001: Scope type '{scopeTypeName}' is already declared for '{existingScope}'.");
                }

                throw new ScopeRegistrationException("GBSR3002",
                    $"GBSR3002: Scope type '{scopeTypeName}' is already declared for '{existingScope}' and cannot be declared for '{scope}'.");
            }

            _declaredScopeTypes.Add(scopeType, scope);
            SetScopeState(scopeType, ScopeLifecycleState.NotLoaded);
            return this;
        }

        private IGameScopeRegistrationBuilder CreateScopeRegistrationBuilder(Type scopeType)
        {
            if (scopeType == null) throw new ArgumentNullException(nameof(scopeType));
            if (!_declaredScopeTypes.TryGetValue(scopeType, out var scope))
            {
                throw new ScopeNotDeclaredException(scopeType);
            }

            return new ScopedRegistrationBuilder(this, scope, scopeType);
        }

        private IGameContextBuilder EnsureRootScopeDefined(GameContextType scope, Type builtInScopeType)
        {
            if (scope != GameContextType.Global && scope != GameContextType.Session)
                throw new ArgumentOutOfRangeException(nameof(scope), scope, "Built-in root scope helpers only support Global and Session.");

            if (FindDeclaredScopeKey(scope) != null)
                return this;

            return DefineScope(builtInScopeType, scope);
        }

        private IGameScopeRegistrationBuilder CreateRootScopeRegistrationBuilder(GameContextType scope, Type builtInScopeType)
        {
            if (scope != GameContextType.Global && scope != GameContextType.Session)
                throw new ArgumentOutOfRangeException(nameof(scope), scope, "Built-in root scope helpers only support Global and Session.");

            var scopeType = FindDeclaredScopeKey(scope);
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

        public IGameContextBuilder OnGlobalInitialized(Action<IGameContext> callback)
        {
            _onGlobalInitialized += callback;
            return this;
        }

        public IGameContextBuilder OnSessionInitialized(Action<IGameContext> callback)
        {
            _onSessionInitialized += callback;
            return this;
        }

        public IGameContextBuilder OnSceneInitialized(Action<IGameContext> callback)
        {
            _onSceneInitialized += callback;
            return this;
        }

        public IGameContextBuilder OnModuleInitialized(Action<IGameContext> callback)
        {
            _onModuleInitialized += callback;
            return this;
        }

        public async Task<IGameContext> BuildAsync(IInitializationProgressNotifier? progressNotifier = null, CancellationToken cancellationToken = default)
        {
            FlushDeferredScopedRegistrations();
            await CancelActiveLoadAsync().ConfigureAwait(false);
            var generation = Interlocked.Increment(ref _runGeneration);
            _activeLoadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var notifier = progressNotifier ?? NullInitializationProgressNotifier.Instance;
            _activeLoadTask = BuildAsyncCore(generation, notifier, _activeLoadCts.Token);
            await _activeLoadTask.ConfigureAwait(false);
            return _globalContext ?? throw new InvalidOperationException("Global context was not created.");
        }

        public async Task RestartSessionAsync(IInitializationProgressNotifier? progressNotifier = null, CancellationToken cancellationToken = default)
        {
            FlushDeferredScopedRegistrations();
            if (_globalContext == null)
                throw new InvalidOperationException("Global context is not created. Call BuildAsync first.");

            await ExecuteScopedOperationAsync(progressNotifier, cancellationToken, RestartSessionAsyncCore).ConfigureAwait(false);
        }

        internal async Task LoadSceneAsync(
            Type sceneScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            FlushDeferredScopedRegistrations();
            ValidateSceneScopeOperationPreconditions(sceneScopeKey);

            await ExecuteScopedOperationAsync(
                    progressNotifier,
                    cancellationToken,
                    (generation, notifier, token) => LoadSceneAsyncCore(sceneScopeKey, generation, notifier, token))
                .ConfigureAwait(false);
        }

        internal async Task LoadModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            FlushDeferredScopedRegistrations();
            ValidateModuleScopeOperationPreconditions(moduleScopeKey);

            await ExecuteScopedOperationAsync(
                    progressNotifier,
                    cancellationToken,
                    (generation, notifier, token) => LoadModuleAsyncCore(moduleScopeKey, generation, notifier, token))
                .ConfigureAwait(false);
        }

        internal async Task ReloadModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            FlushDeferredScopedRegistrations();
            ValidateModuleScopeOperationPreconditions(moduleScopeKey);

            await ExecuteScopedOperationAsync(
                    progressNotifier,
                    cancellationToken,
                    (generation, notifier, token) => ReloadModuleAsyncCore(moduleScopeKey, generation, notifier, token))
                .ConfigureAwait(false);
        }

        public async Task PreloadSceneAsync(
            Type sceneScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            FlushDeferredScopedRegistrations();
            ValidateSceneScopeOperationPreconditions(sceneScopeKey);

            var notifier = progressNotifier ?? NullInitializationProgressNotifier.Instance;
            var sceneProfile = _sceneProfiles[sceneScopeKey];

            var initializedServices = new HashSet<Type>();
            var availableServices = new Dictionary<Type, object>();
            SeedInitializedFromContext(_globalContext, initializedServices, availableServices);
            SeedInitializedFromContext(_sessionContext, initializedServices, availableServices);

            GameContext? preloadedContext = null;
            try
            {
                preloadedContext = await CreateAndInitializeScopeContextAsync(
                        GameContextType.Scene,
                        _sessionContext!,
                        sceneProfile.Registrations,
                        sceneProfile.Services,
                        _onSceneInitialized,
                        initializedServices,
                        availableServices,
                        notifier,
                        Volatile.Read(ref _runGeneration),
                        cancellationToken,
                        sceneScopeKey,
                        skipActivation: true)
                    .ConfigureAwait(false);

                if (_preloadedContexts.TryGetValue(sceneScopeKey, out var existingPreloadedSceneContext))
                {
                    await DisposeScopeContextAsync(
                            GameContextType.Scene,
                            existingPreloadedSceneContext,
                            cancellationToken,
                            sceneScopeKey)
                        .ConfigureAwait(false);
                }

                _preloadedContexts[sceneScopeKey] = preloadedContext;
            }
            catch
            {
                await DisposeContextAsync(preloadedContext, cancellationToken).ConfigureAwait(false);
                preloadedContext = null;
                throw;
            }
        }

        public async Task PreloadModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            FlushDeferredScopedRegistrations();
            ValidateModuleScopeOperationPreconditions(moduleScopeKey);

            var notifier = progressNotifier ?? NullInitializationProgressNotifier.Instance;
            var moduleProfile = _moduleProfiles[moduleScopeKey];

            var initializedServices = new HashSet<Type>();
            var availableServices = new Dictionary<Type, object>();
            SeedInitializedFromContext(_globalContext, initializedServices, availableServices);
            SeedInitializedFromContext(_sessionContext, initializedServices, availableServices);
            SeedInitializedFromContext(_sceneContext, initializedServices, availableServices);

            GameContext? preloadedContext = null;
            try
            {
                preloadedContext = await CreateAndInitializeScopeContextAsync(
                        GameContextType.Module,
                        _sceneContext!,
                        moduleProfile.Registrations,
                        moduleProfile.Services,
                        _onModuleInitialized,
                        initializedServices,
                        availableServices,
                        notifier,
                        Volatile.Read(ref _runGeneration),
                        cancellationToken,
                        moduleScopeKey,
                        skipActivation: true)
                    .ConfigureAwait(false);

                if (_preloadedContexts.TryGetValue(moduleScopeKey, out var existingPreloadedModuleContext))
                {
                    await DisposeScopeContextAsync(
                            GameContextType.Module,
                            existingPreloadedModuleContext,
                            cancellationToken,
                            moduleScopeKey)
                        .ConfigureAwait(false);
                }

                _preloadedContexts[moduleScopeKey] = preloadedContext;
            }
            catch
            {
                await DisposeContextAsync(preloadedContext, cancellationToken).ConfigureAwait(false);
                preloadedContext = null;
                throw;
            }
        }

        public bool HasPreloadedScope(Type scopeKey)
        {
            if (scopeKey == null) throw new ArgumentNullException(nameof(scopeKey));
            return _preloadedContexts.ContainsKey(scopeKey);
        }

        public async Task LoadAdditiveModuleAsync(
            Type moduleScopeKey,
            IInitializationProgressNotifier? progressNotifier = null,
            CancellationToken cancellationToken = default)
        {
            FlushDeferredScopedRegistrations();
            ValidateModuleScopeOperationPreconditions(moduleScopeKey);

            if (_additiveModuleContexts.ContainsKey(moduleScopeKey))
                throw new InvalidOperationException($"Additive module scope '{moduleScopeKey.Name}' is already loaded.");

            var notifier = progressNotifier ?? NullInitializationProgressNotifier.Instance;
            var moduleProfile = _moduleProfiles[moduleScopeKey];

            var initializedServices = new HashSet<Type>();
            var availableServices = new Dictionary<Type, object>();
            SeedInitializedFromContext(_globalContext, initializedServices, availableServices);
            SeedInitializedFromContext(_sessionContext, initializedServices, availableServices);
            SeedInitializedFromContext(_sceneContext, initializedServices, availableServices);

            GameContext? moduleContext = null;
            try
            {
                moduleContext = await CreateAndInitializeScopeContextAsync(
                        GameContextType.Module,
                        _sceneContext!,
                        moduleProfile.Registrations,
                        moduleProfile.Services,
                        _onModuleInitialized,
                        initializedServices,
                        availableServices,
                        notifier,
                        Volatile.Read(ref _runGeneration),
                        cancellationToken,
                        moduleScopeKey)
                    .ConfigureAwait(false);

                _additiveModuleContexts[moduleScopeKey] = moduleContext;
            }
            catch
            {
                await DisposeContextAsync(moduleContext, cancellationToken).ConfigureAwait(false);
                moduleContext = null;
                throw;
            }
        }

        public async Task UnloadAdditiveModuleAsync(
            Type moduleScopeKey,
            CancellationToken cancellationToken = default)
        {
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            if (!_additiveModuleContexts.TryGetValue(moduleScopeKey, out var context))
                throw new InvalidOperationException($"Additive module scope '{moduleScopeKey.Name}' is not loaded.");

            SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Deactivating, moduleScopeKey);
            await ExecuteScopeActivationExitAsync(GameContextType.Module, context, NullInitializationProgressNotifier.Instance, cancellationToken).ConfigureAwait(false);
            await DisposeScopeContextAsync(
                    GameContextType.Module,
                    context,
                    cancellationToken,
                    moduleScopeKey,
                    () => SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Disposed, moduleScopeKey))
                .ConfigureAwait(false);
            _additiveModuleContexts.Remove(moduleScopeKey);
        }

        internal bool TryResolveFromSession<TService>(out TService? service)
            where TService : class
        {
            if (_sessionContext == null)
                throw new InvalidOperationException("Session context is not initialized. Call BuildAsync first.");

            if (!_sessionContext.IsRegistered(typeof(TService)))
            {
                service = null;
                return false;
            }

            try
            {
                service = _sessionContext.Resolve<TService>();
                return true;
            }
            catch (VContainerException)
            {
                service = null;
                return false;
            }
        }

        internal IGameContext GetSessionContext()
        {
            return _sessionContext ?? throw new InvalidOperationException("Session context is not initialized. Call BuildAsync first.");
        }

        internal async Task EnsureLazyServiceInitializedAsync(Type serviceType, CancellationToken cancellationToken = default)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));

            if (_initializedLazyServices.Contains(serviceType))
                return;

            if (!_lazyServiceBindings.TryGetValue(serviceType, out var entry))
                return;

            await _lazyInitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_initializedLazyServices.Contains(serviceType))
                    return;

                var resolved = entry.Context.Resolve(serviceType);
                if (resolved is IAsyncInitializableService asyncService)
                    await asyncService.InitializeAsync(cancellationToken).ConfigureAwait(false);

                _initializedLazyServices.Add(serviceType);
                RegisterInitializedServiceForScopeDisposal(entry.Scope, entry.ScopeKey, serviceType);
            }
            finally
            {
                _lazyInitLock.Release();
            }
        }

        internal bool CanRestartSession()
        {
            return _globalContext != null;
        }

        internal void SetScopeState(Type scopeType, ScopeLifecycleState state)
        {
            lock (_scopeStateSync)
            {
                _scopeStates[scopeType] = state;
            }
        }

        internal ScopeLifecycleState GetScopeState(Type scopeType)
        {
            lock (_scopeStateSync)
            {
                return _scopeStates.TryGetValue(scopeType, out var state) ? state : ScopeLifecycleState.NotLoaded;
            }
        }

        private void SetScopeStateIfTracked(GameContextType scope, ScopeLifecycleState state, Type? explicitScopeKey = null)
        {
            if (explicitScopeKey != null)
            {
                SetScopeState(explicitScopeKey, state);
                return;
            }

            foreach (var key in FindDeclaredScopeKeys(scope))
                SetScopeState(key, state);
        }

        private Type? FindDeclaredScopeKey(GameContextType scope)
        {
            foreach (var kvp in _declaredScopeTypes)
            {
                if (kvp.Value == scope)
                    return kvp.Key;
            }

            return null;
        }

        private IEnumerable<Type> FindDeclaredScopeKeys(GameContextType scope)
        {
            foreach (var kvp in _declaredScopeTypes)
            {
                if (kvp.Value == scope)
                    yield return kvp.Key;
            }
        }

        private void ValidateSceneScopeOperationPreconditions(Type sceneScopeKey)
        {
            if (sceneScopeKey == null) throw new ArgumentNullException(nameof(sceneScopeKey));
            if (_sessionContext == null)
                throw new InvalidOperationException("Session context is not initialized. Call BuildAsync first.");
            if (!_sceneProfiles.ContainsKey(sceneScopeKey))
                throw new InvalidOperationException($"Scene scope '{sceneScopeKey.Name}' is not configured.");
        }

        private void ValidateModuleScopeOperationPreconditions(Type moduleScopeKey)
        {
            if (moduleScopeKey == null) throw new ArgumentNullException(nameof(moduleScopeKey));
            if (_sceneContext == null)
                throw new InvalidOperationException("Scene context is not initialized. Call LoadSceneAsync first.");
            if (!_moduleProfiles.ContainsKey(moduleScopeKey))
                throw new InvalidOperationException($"Module scope '{moduleScopeKey.Name}' is not configured.");
        }

        private async Task ExecuteScopedOperationAsync(
            IInitializationProgressNotifier? progressNotifier,
            CancellationToken cancellationToken,
            Func<long, IInitializationProgressNotifier, CancellationToken, Task> operation)
        {
            await CancelActiveLoadAsync().ConfigureAwait(false);
            var generation = Interlocked.Increment(ref _runGeneration);
            _activeLoadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var notifier = progressNotifier ?? NullInitializationProgressNotifier.Instance;
            _activeLoadTask = operation(generation, notifier, _activeLoadCts.Token);
            await _activeLoadTask.ConfigureAwait(false);
        }

        private async Task BuildAsyncCore(long generation, IInitializationProgressNotifier progressNotifier, CancellationToken cancellationToken)
        {
            ValidateExternalGlobalConfiguration();
            await DisposeAdditiveModuleContextsAsync(cancellationToken).ConfigureAwait(false);
            await DisposePreloadedContextsAsync(cancellationToken).ConfigureAwait(false);

            if (_moduleContext != null)
            {
                await DisposeScopeContextAsync(GameContextType.Module, _moduleContext, cancellationToken, _activeModuleScopeKey).ConfigureAwait(false);
                _moduleContext = null;
            }

            if (_sceneContext != null)
            {
                await DisposeScopeContextAsync(GameContextType.Scene, _sceneContext, cancellationToken, _activeSceneScopeKey).ConfigureAwait(false);
                _sceneContext = null;
            }

            if (_sessionContext != null)
            {
                await DisposeScopeContextAsync(GameContextType.Session, _sessionContext, cancellationToken).ConfigureAwait(false);
                _sessionContext = null;
            }

            if (_ownsGlobalContext)
            {
                await DisposeContextAsync(_globalContext, cancellationToken).ConfigureAwait(false);
                _globalContext = null;
            }

            var initializedServices = new HashSet<Type>();
            var availableServices = new Dictionary<Type, object>();
            _logger.LogInformation("BuildAsync started — initializing scopes");

            IGameContext? globalContext = null;
            GameContext? sessionContext = null;
            GameContext? sceneContext = null;
            GameContext? moduleContext = null;
            Type? activeSceneScopeKey = null;
            Type? activeModuleScopeKey = null;

            try
            {
                ThrowIfStaleGeneration(generation, cancellationToken);
                if (_ownsGlobalContext)
                {
                    _logger.LogDebug("Building scope {Scope}", GameContextType.Global);
                    SetScopeStateIfTracked(GameContextType.Global, ScopeLifecycleState.Loading);
                    _globalEventBus = new ScopeEventBus();
                    globalContext = CreateContext(
                        null,
                        _globalRegistrations,
                        Array.Empty<ServiceDescriptor>(),
                        _onGlobalInitialized,
                        initialize: true,
                        availableServices,
                        _globalEventBus);
                    var globalTotalServices = await ExecuteInitializersAsync(
                            GameContextType.Global,
                            (GameContext)globalContext,
                            initializedServices,
                            progressNotifier,
                            generation,
                            cancellationToken)
                        .ConfigureAwait(false);
                    progressNotifier.OnScopeCompleted(GameContextType.Global, globalTotalServices);
                    SetScopeStateIfTracked(GameContextType.Global, ScopeLifecycleState.Active);
                    _globalContext = globalContext;
                    globalContext = null;
                }
                else
                {
                    globalContext = _globalContext
                        ?? throw new InvalidOperationException("External global context is not configured.");
                }

                ThrowIfStaleGeneration(generation, cancellationToken);
                await ExecuteStageCallbackOnMainThreadAsync(
                        progressNotifier.OnGlobalContextReadyForSessionInitializationAsync,
                        cancellationToken)
                    .ConfigureAwait(false);
                ThrowIfStaleGeneration(generation, cancellationToken);
                _sessionEventBus = new ScopeEventBus(_globalEventBus);
                sessionContext = await CreateAndInitializeScopeContextAsync(
                        GameContextType.Session,
                        (globalContext ?? _globalContext)!,
                        _sessionRegistrations,
                        Array.Empty<ServiceDescriptor>(),
                        _onSessionInitialized,
                        initializedServices,
                        availableServices,
                        progressNotifier,
                        generation,
                        cancellationToken,
                        eventBus: _sessionEventBus)
                    .ConfigureAwait(false);

                ThrowIfStaleGeneration(generation, cancellationToken);
                _sessionContext = sessionContext;
                _sceneContext = sceneContext;
                _moduleContext = moduleContext;
                _activeSceneScopeKey = activeSceneScopeKey;
                _activeModuleScopeKey = activeModuleScopeKey;
            }
            catch
            {
                await DisposeContextAsync(moduleContext, cancellationToken).ConfigureAwait(false);
                moduleContext = null;
                await DisposeContextAsync(sceneContext, cancellationToken).ConfigureAwait(false);
                sceneContext = null;
                await DisposeContextAsync(sessionContext, cancellationToken).ConfigureAwait(false);
                sessionContext = null;
                if (_ownsGlobalContext)
                {
                    SetScopeStateIfTracked(GameContextType.Global, ScopeLifecycleState.Failed);
                    await DisposeContextAsync(globalContext, cancellationToken).ConfigureAwait(false);
                    globalContext = null;
                }
                throw;
            }
        }

        private void ValidateExternalGlobalConfiguration()
        {
            if (_ownsGlobalContext)
                return;

            if (_globalRegistrations.Count > 0)
            {
                throw new InvalidOperationException(
                    "GBBR1001: Global registrations are not allowed when using an external global context bridge.");
            }
        }

        private async Task RestartSessionAsyncCore(long generation, IInitializationProgressNotifier progressNotifier, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Session restarting");
            _lazyServiceBindings.Clear();
            _initializedLazyServices.Clear();
            _scopeStates.Clear();

            await DisposeAdditiveModulesAsync(cancellationToken).ConfigureAwait(false);
            await DisposePreloadedContextsAsync(cancellationToken).ConfigureAwait(false);

            if (_moduleContext != null)
            {
                SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Reloading, _activeModuleScopeKey);
                await ExecuteScopeActivationExitAsync(GameContextType.Module, _moduleContext, progressNotifier, cancellationToken).ConfigureAwait(false);
                await DisposeScopeContextAsync(
                        GameContextType.Module,
                        _moduleContext,
                        cancellationToken,
                        _activeModuleScopeKey,
                        () => SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Disposed, _activeModuleScopeKey))
                    .ConfigureAwait(false);
                _moduleContext = null;
            }

            if (_sceneContext != null)
            {
                SetScopeStateIfTracked(GameContextType.Scene, ScopeLifecycleState.Reloading, _activeSceneScopeKey);
                await ExecuteScopeActivationExitAsync(GameContextType.Scene, _sceneContext, progressNotifier, cancellationToken).ConfigureAwait(false);
                await DisposeScopeContextAsync(
                        GameContextType.Scene,
                        _sceneContext,
                        cancellationToken,
                        _activeSceneScopeKey,
                        () => SetScopeStateIfTracked(GameContextType.Scene, ScopeLifecycleState.Disposed, _activeSceneScopeKey))
                    .ConfigureAwait(false);
                _sceneContext = null;
            }

            if (_sessionContext != null)
            {
                SetScopeStateIfTracked(GameContextType.Session, ScopeLifecycleState.Reloading);
                await ExecuteScopeActivationExitAsync(GameContextType.Session, _sessionContext, progressNotifier, cancellationToken).ConfigureAwait(false);
                await DisposeScopeContextAsync(
                        GameContextType.Session,
                        _sessionContext,
                        cancellationToken,
                        onDisposed: () => SetScopeStateIfTracked(GameContextType.Session, ScopeLifecycleState.Disposed))
                    .ConfigureAwait(false);
                _sessionContext = null;
            }

            var initializedServices = new HashSet<Type>();
            var availableServices = new Dictionary<Type, object>();
            SeedInitializedFromContext(_globalContext, initializedServices, availableServices);

            GameContext? sessionContext = null;
            GameContext? sceneContext = null;
            GameContext? moduleContext = null;

            try
            {
                _moduleEventBus?.Dispose();
                _moduleEventBus = null;
                _sceneEventBus?.Dispose();
                _sceneEventBus = null;
                _sessionEventBus?.Dispose();
                _sessionEventBus = null;

                await DrainMainThreadFrameAsync(cancellationToken).ConfigureAwait(false);
                ThrowIfStaleGeneration(generation, cancellationToken);
                await ExecuteStageCallbackOnMainThreadAsync(
                        progressNotifier.OnSessionRestartTeardownCompletedAsync,
                        cancellationToken)
                    .ConfigureAwait(false);
                ThrowIfStaleGeneration(generation, cancellationToken);
                _sessionEventBus = new ScopeEventBus(_globalEventBus);
                sessionContext = await CreateAndInitializeScopeContextAsync(
                        GameContextType.Session,
                        _globalContext!,
                        _sessionRegistrations,
                        Array.Empty<ServiceDescriptor>(),
                        _onSessionInitialized,
                        initializedServices,
                        availableServices,
                        progressNotifier,
                        generation,
                        cancellationToken,
                        eventBus: _sessionEventBus)
                    .ConfigureAwait(false);

                if (_activeSceneScopeKey != null && TryGetScopeProfile(_sceneProfiles, _activeSceneScopeKey, out var sceneProfile))
                {
                    _sceneEventBus = new ScopeEventBus(_sessionEventBus);
                    sceneContext = await CreateAndInitializeScopeContextAsync(
                            GameContextType.Scene,
                            sessionContext,
                            sceneProfile.Registrations,
                            sceneProfile.Services,
                            _onSceneInitialized,
                            initializedServices,
                            availableServices,
                            progressNotifier,
                            generation,
                            cancellationToken,
                            _activeSceneScopeKey,
                            eventBus: _sceneEventBus)
                        .ConfigureAwait(false);
                }

                if (_activeModuleScopeKey != null && sceneContext != null && TryGetScopeProfile(_moduleProfiles, _activeModuleScopeKey, out var moduleProfile))
                {
                    _moduleEventBus = new ScopeEventBus(_sceneEventBus);
                    moduleContext = await CreateAndInitializeScopeContextAsync(
                            GameContextType.Module,
                            sceneContext,
                            moduleProfile.Registrations,
                            moduleProfile.Services,
                            _onModuleInitialized,
                            initializedServices,
                            availableServices,
                            progressNotifier,
                            generation,
                            cancellationToken,
                            _activeModuleScopeKey,
                            eventBus: _moduleEventBus)
                        .ConfigureAwait(false);
                }

                ThrowIfStaleGeneration(generation, cancellationToken);
                _sessionContext = sessionContext;
                _sceneContext = sceneContext;
                _moduleContext = moduleContext;
            }
            catch
            {
                await DisposeContextAsync(moduleContext, cancellationToken).ConfigureAwait(false);
                moduleContext = null;
                await DisposeContextAsync(sceneContext, cancellationToken).ConfigureAwait(false);
                sceneContext = null;
                await DisposeContextAsync(sessionContext, cancellationToken).ConfigureAwait(false);
                sessionContext = null;
                throw;
            }
        }

        private async Task LoadSceneAsyncCore(
            Type sceneScopeKey,
            long generation,
            IInitializationProgressNotifier progressNotifier,
            CancellationToken cancellationToken)
        {
            var sceneProfile = _sceneProfiles[sceneScopeKey];

            await DisposeAdditiveModulesAsync(cancellationToken).ConfigureAwait(false);

            if (_moduleContext != null)
            {
                SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Deactivating, _activeModuleScopeKey);
                await ExecuteScopeActivationExitAsync(GameContextType.Module, _moduleContext, progressNotifier, cancellationToken).ConfigureAwait(false);
                await DisposeScopeContextAsync(
                        GameContextType.Module,
                        _moduleContext,
                        cancellationToken,
                        _activeModuleScopeKey,
                        () => SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Disposed, _activeModuleScopeKey))
                    .ConfigureAwait(false);
                _moduleContext = null;
            }

            if (_sceneContext != null)
            {
                SetScopeStateIfTracked(GameContextType.Scene, ScopeLifecycleState.Deactivating, _activeSceneScopeKey);
                await ExecuteScopeActivationExitAsync(GameContextType.Scene, _sceneContext, progressNotifier, cancellationToken).ConfigureAwait(false);
                await DisposeScopeContextAsync(
                        GameContextType.Scene,
                        _sceneContext,
                        cancellationToken,
                        _activeSceneScopeKey,
                        () => SetScopeStateIfTracked(GameContextType.Scene, ScopeLifecycleState.Disposed, _activeSceneScopeKey))
                    .ConfigureAwait(false);
                _sceneContext = null;
            }

            if (_preloadedContexts.TryGetValue(sceneScopeKey, out var preloaded))
            {
                _preloadedContexts.Remove(sceneScopeKey);
                await ExecuteScopeActivationEnterAsync(GameContextType.Scene, preloaded, progressNotifier, 0, cancellationToken).ConfigureAwait(false);
                ThrowIfStaleGeneration(generation, cancellationToken);
                _sceneContext = preloaded;
                _moduleContext = null;
                _activeSceneScopeKey = sceneScopeKey;
                _activeModuleScopeKey = null;
                SetScopeStateIfTracked(GameContextType.Scene, ScopeLifecycleState.Active, sceneScopeKey);
                return;
            }

            var initializedServices = new HashSet<Type>();
            var availableServices = new Dictionary<Type, object>();
            SeedInitializedFromContext(_globalContext, initializedServices, availableServices);
            SeedInitializedFromContext(_sessionContext, initializedServices, availableServices);

            GameContext? sceneContext = null;
            try
            {
                sceneContext = await CreateAndInitializeScopeContextAsync(
                        GameContextType.Scene,
                        _sessionContext!,
                        sceneProfile.Registrations,
                        sceneProfile.Services,
                        _onSceneInitialized,
                        initializedServices,
                        availableServices,
                        progressNotifier,
                        generation,
                        cancellationToken,
                        sceneScopeKey)
                    .ConfigureAwait(false);

                ThrowIfStaleGeneration(generation, cancellationToken);
                _sceneContext = sceneContext;
                _moduleContext = null;
                _activeSceneScopeKey = sceneScopeKey;
                _activeModuleScopeKey = null;
            }
            catch
            {
                await DisposeContextAsync(sceneContext, cancellationToken).ConfigureAwait(false);
                sceneContext = null;
                throw;
            }
        }

        private async Task LoadModuleAsyncCore(
            Type moduleScopeKey,
            long generation,
            IInitializationProgressNotifier progressNotifier,
            CancellationToken cancellationToken)
        {
            var moduleProfile = _moduleProfiles[moduleScopeKey];

            if (_moduleContext != null)
            {
                SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Deactivating, _activeModuleScopeKey);
                await ExecuteScopeActivationExitAsync(GameContextType.Module, _moduleContext, progressNotifier, cancellationToken).ConfigureAwait(false);
                await DisposeScopeContextAsync(
                        GameContextType.Module,
                        _moduleContext,
                        cancellationToken,
                        _activeModuleScopeKey,
                        () => SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Disposed, _activeModuleScopeKey))
                    .ConfigureAwait(false);
                _moduleContext = null;
            }

            if (_preloadedContexts.TryGetValue(moduleScopeKey, out var preloaded))
            {
                _preloadedContexts.Remove(moduleScopeKey);
                await ExecuteScopeActivationEnterAsync(GameContextType.Module, preloaded, progressNotifier, 0, cancellationToken).ConfigureAwait(false);
                ThrowIfStaleGeneration(generation, cancellationToken);
                _moduleContext = preloaded;
                _activeModuleScopeKey = moduleScopeKey;
                SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Active, moduleScopeKey);
                return;
            }

            var initializedServices = new HashSet<Type>();
            var availableServices = new Dictionary<Type, object>();
            SeedInitializedFromContext(_globalContext, initializedServices, availableServices);
            SeedInitializedFromContext(_sessionContext, initializedServices, availableServices);
            SeedInitializedFromContext(_sceneContext, initializedServices, availableServices);

            GameContext? moduleContext = null;
            try
            {
                moduleContext = await CreateAndInitializeScopeContextAsync(
                        GameContextType.Module,
                        _sceneContext!,
                        moduleProfile.Registrations,
                        moduleProfile.Services,
                        _onModuleInitialized,
                        initializedServices,
                        availableServices,
                        progressNotifier,
                        generation,
                        cancellationToken,
                        moduleScopeKey)
                    .ConfigureAwait(false);

                ThrowIfStaleGeneration(generation, cancellationToken);
                _moduleContext = moduleContext;
                _activeModuleScopeKey = moduleScopeKey;
            }
            catch
            {
                await DisposeContextAsync(moduleContext, cancellationToken).ConfigureAwait(false);
                moduleContext = null;
                throw;
            }
        }

        private Task ReloadModuleAsyncCore(
            Type moduleScopeKey,
            long generation,
            IInitializationProgressNotifier progressNotifier,
            CancellationToken cancellationToken)
        {
            return LoadModuleAsyncCore(moduleScopeKey, generation, progressNotifier, cancellationToken);
        }

        private async Task<GameContext> CreateAndInitializeScopeContextAsync(
            GameContextType scope,
            IGameContext parentContext,
            IReadOnlyCollection<Action<IGameContext>> registrations,
            IReadOnlyCollection<ServiceDescriptor> autoServices,
            Action<IGameContext>? initializedCallback,
            ISet<Type> initializedServices,
            IDictionary<Type, object> availableServices,
            IInitializationProgressNotifier progressNotifier,
            long generation,
            CancellationToken cancellationToken,
            Type? scopeKey = null,
            bool skipActivation = false,
            ScopeEventBus? eventBus = null)
        {
            _logger.LogDebug("Building scope {Scope}", scope);
            SetScopeStateIfTracked(scope, ScopeLifecycleState.Loading, scopeKey);
            ThrowIfStaleGeneration(generation, cancellationToken);
            GameContext context;
            var scopeStopwatch = Stopwatch.StartNew();
            try
            {
                context = CreateContext(parentContext, registrations, autoServices, initializedCallback, initialize: true, availableServices, eventBus);
                var totalServices = await ExecuteInitializersAsync(scope, context, initializedServices, progressNotifier, generation, cancellationToken, scopeKey).ConfigureAwait(false);
                ThrowIfStaleGeneration(generation, cancellationToken);
                if (scope != GameContextType.Global && !skipActivation)
                {
                    await ExecuteScopeActivationEnterAsync(scope, context, progressNotifier, totalServices, cancellationToken).ConfigureAwait(false);
                }

                progressNotifier.OnScopeCompleted(scope, totalServices);
                ThrowIfStaleGeneration(generation, cancellationToken);
                scopeStopwatch.Stop();
                _logger.LogInformation("Scope {Scope} initialized ({ServiceCount} services, {Duration}s)", scope, totalServices, scopeStopwatch.Elapsed.TotalSeconds.ToString("F2"));
            }
            catch
            {
                SetScopeStateIfTracked(scope, ScopeLifecycleState.Failed, scopeKey);
                throw;
            }

            SetScopeStateIfTracked(scope, skipActivation ? ScopeLifecycleState.Preloaded : ScopeLifecycleState.Active, scopeKey);
            return context;
        }

        private void SeedInitializedFromContext(
            IGameContext? context,
            ISet<Type> initializedServices,
            IDictionary<Type, object> availableServices)
        {
            if (context is not GameContext gameContext)
                return;

            foreach (var initializer in DiscoverInitializers(gameContext))
            {
                initializedServices.Add(initializer.ServiceType);
                availableServices[initializer.ServiceType] = gameContext.Resolve(initializer.ServiceType);
            }
        }

        private static GameContext CreateContext(
            IGameContext? parent,
            IReadOnlyCollection<Action<IGameContext>> registrations,
            IReadOnlyCollection<ServiceDescriptor> autoServices,
            Action<IGameContext>? initializedCallback,
            bool initialize,
            IDictionary<Type, object> availableServices,
            ScopeEventBus? eventBus = null)
        {
            var context = new GameContext(parent);
            foreach (var registration in registrations)
                registration(context);

            if (eventBus != null)
            {
                context.RegisterInstance<IScopeEventBus>(eventBus);
                context.OnBeforeDispose += eventBus.Dispose;
            }

            RegisterAutoServices(context, autoServices, availableServices);

            if (initializedCallback != null)
                context.OnInitialized += () => initializedCallback(context);
            if (initialize)
                context.Initialize();
            return context;
        }

        private static void RegisterAutoServices(
            GameContext context,
            IReadOnlyCollection<ServiceDescriptor> autoServices,
            IDictionary<Type, object> availableServices)
        {
            if (autoServices.Count == 0)
                return;

            var pending = autoServices
                .Select(descriptor => new ServiceConstructionBinding(
                    descriptor.ServiceType,
                    descriptor.ImplementationType,
                    InitializationGraphRules.ResolveConstructorDependencies(descriptor.ImplementationType)))
                .ToList();

            foreach (var binding in pending)
            {
                foreach (var dependency in binding.Dependencies)
                {
                    var isKnown = pending.Any(item => item.ServiceType == dependency)
                                  || availableServices.ContainsKey(dependency)
                                  || context.TryGetRegisteredInstance(dependency, out _);
                    if (!isKnown)
                    {
                        var knownServices = string.Join(", ", pending.Select(item => item.ServiceType.Name).Distinct());
                        throw new InvalidOperationException(
                            $"Service {binding.ServiceType.Name} depends on {dependency.Name}, but dependency is not registered. Known services: {knownServices}");
                    }
                }
            }

            var createdInstances = new Dictionary<Type, object>();
            while (pending.Count > 0)
            {
                var ready = pending
                    .Where(binding => binding.Dependencies.All(dependency =>
                        availableServices.ContainsKey(dependency) || context.TryGetRegisteredInstance(dependency, out _)))
                    .ToArray();

                if (ready.Length == 0)
                {
                    var unresolved = string.Join(", ", pending.Select(binding => binding.ServiceType.Name).Distinct());
                    throw new InvalidOperationException($"Constructor dependency cycle detected. Remaining services: {unresolved}");
                }

                foreach (var group in ready.GroupBy(binding => binding.ImplementationType))
                {
                    var exemplar = group.First();
                    if (!createdInstances.TryGetValue(exemplar.ImplementationType, out var instance))
                    {
                        instance = CreateServiceInstance(context, exemplar, availableServices);
                        createdInstances[exemplar.ImplementationType] = instance;
                    }

                    var serviceTypes = group.Select(binding => binding.ServiceType).Distinct().ToArray();
                    context.RegisterInstanceEx(exemplar.ImplementationType, instance, serviceTypes, ownsLifetime: true);

                    foreach (var binding in group)
                    {
                        if (!availableServices.ContainsKey(binding.ServiceType))
                            availableServices[binding.ServiceType] = instance;
                        pending.Remove(binding);
                    }
                }
            }
        }

        private static object CreateServiceInstance(
            GameContext context,
            ServiceConstructionBinding binding,
            IDictionary<Type, object> availableServices)
        {
            var constructor = InitializationGraphRules.SelectConstructor(binding.ImplementationType);
            if (constructor == null)
            {
                return Activator.CreateInstance(binding.ImplementationType)
                       ?? throw new InvalidOperationException($"Failed to create {binding.ImplementationType.Name}.");
            }

            var arguments = constructor.GetParameters()
                .Select(parameter => ResolveConstructorParameter(context, parameter, availableServices))
                .ToArray();
            return constructor.Invoke(arguments);
        }

        private static object ResolveConstructorParameter(
            GameContext context,
            ParameterInfo parameter,
            IDictionary<Type, object> availableServices)
        {
            var parameterType = parameter.ParameterType;
            if (availableServices.TryGetValue(parameterType, out var available))
                return available;
            if (context.TryGetRegisteredInstance(parameterType, out var localInstance))
                return localInstance;
            if (TryResolveFromParent(context.Parent, parameterType, out var parentValue))
                return parentValue;
            if (parameter.HasDefaultValue)
                return parameter.DefaultValue!;

            throw new InvalidOperationException(
                $"Cannot resolve constructor dependency {parameterType.Name} for {context.GetType().Name}.");
        }

        private static bool TryResolveFromParent(IGameContext? parent, Type serviceType, out object resolved)
        {
            if (parent == null)
            {
                resolved = null!;
                return false;
            }

            try
            {
                resolved = parent.Resolve(serviceType);
                return true;
            }
            catch (VContainerException)
            {
                resolved = null!;
                return false;
            }
            catch (InvalidOperationException)
            {
                resolved = null!;
                return false;
            }
        }

        private async Task DisposeContextAsync(GameContext? context, CancellationToken cancellationToken)
        {
            if (context == null)
                return;

            await _executionScheduler.ExecuteAsync(
                    InitializationThreadAffinity.MainThread,
                    _ =>
                    {
                        context.Dispose();
                        return Task.CompletedTask;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task DisposeContextAsync(IGameContext? context, CancellationToken cancellationToken)
        {
            if (context == null)
                return;

            await _executionScheduler.ExecuteAsync(
                    InitializationThreadAffinity.MainThread,
                    _ =>
                    {
                        context.Dispose();
                        return Task.CompletedTask;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task DisposeScopeContextAsync(
            GameContextType scope,
            GameContext? context,
            CancellationToken cancellationToken,
            Type? scopeKey = null,
            Action? onDisposed = null)
        {
            if (context == null)
                return;

            List<Exception>? exceptions = null;

            try
            {
                await DisposeScopeServicesAsync(scope, context, cancellationToken, scopeKey).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                exceptions ??= new List<Exception>();
                exceptions.Add(ex);
            }

            try
            {
                await DisposeContextAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                exceptions ??= new List<Exception>();
                exceptions.Add(ex);
            }

            onDisposed?.Invoke();

            if (exceptions != null)
                throw new AggregateException(exceptions);
        }

        private void RegisterInitializedServiceForScopeDisposal(GameContextType scope, Type? scopeKey, Type serviceType)
        {
            var key = (scope, scopeKey);
            if (!_scopeInitializationOrder.TryGetValue(key, out var initOrder))
            {
                initOrder = new List<Type>();
                _scopeInitializationOrder[key] = initOrder;
            }

            if (!initOrder.Contains(serviceType))
            {
                initOrder.Add(serviceType);
            }
        }

        private async Task DisposeScopeServicesAsync(
            GameContextType scope,
            GameContext context,
            CancellationToken cancellationToken,
            Type? scopeKey = null)
        {
            var key = (scope, scopeKey);
            _scopeInitializationOrder.TryGetValue(key, out var initOrder);

            var exceptions = (List<Exception>?)null;

            for (var i = (initOrder?.Count ?? 0) - 1; i >= 0; i--)
            {
                try
                {
                    var serviceType = initOrder![i];
                    if (context.TryGetRegisteredInstance(serviceType, out _))
                    {
                        continue;
                    }

                    var resolved = context.Resolve(serviceType);
                    var affinity = resolved is IInitializationThreadAffinityProvider affinityProvider
                        ? affinityProvider.ThreadAffinity
                        : InitializationThreadAffinity.MainThread;
                    if (resolved is IAsyncDisposableService disposableService)
                    {
                        await _executionScheduler.ExecuteAsync(
                                affinity,
                                token => disposableService.DisposeAsync(token),
                                cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (resolved is IAsyncDisposable asyncDisposable)
                    {
                        await _executionScheduler.ExecuteAsync(
                                affinity,
                                _ => asyncDisposable.DisposeAsync().AsTask(),
                                cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (resolved is IDisposable disposable)
                    {
                        await _executionScheduler.ExecuteAsync(
                                affinity,
                                _ =>
                                {
                                    disposable.Dispose();
                                    return Task.CompletedTask;
                                },
                                cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                }
                catch (Exception ex)
                {
                    exceptions ??= new List<Exception>();
                    exceptions.Add(ex);
                }
            }

            _scopeInitializationOrder.Remove(key);

            if (exceptions != null)
                throw new AggregateException(exceptions);
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();

            public new bool Equals(object? x, object? y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }

        private async Task<int> ExecuteInitializersAsync(
            GameContextType scope,
            GameContext context,
            ISet<Type> initializedServices,
            IInitializationProgressNotifier progressNotifier,
            long generation,
            CancellationToken cancellationToken,
            Type? scopeKey = null)
        {
            var initializers = DiscoverInitializers(context);

            var lazyBindings = initializers
                .Where(b => typeof(ILazyInitializableService).IsAssignableFrom(b.ImplementationType))
                .ToList();
            foreach (var lazy in lazyBindings)
            {
                initializers.Remove(lazy);
                _lazyServiceBindings[lazy.ServiceType] = (context, scope, scopeKey);
            }

            var totalServices = initializers.Count;
            progressNotifier.OnScopeStarted(scope, totalServices);
            if (totalServices == 0)
            {
                return totalServices;
            }

            var pending = initializers.ToDictionary(initializer => initializer.ServiceType);
            ValidateDependencies(scope, context, pending, initializedServices);

            var initOrder = new List<Type>();
            var completedServices = 0;
            while (pending.Count > 0)
            {
                ThrowIfStaleGeneration(generation, cancellationToken);
                var ready = pending.Values
                    .Where(initializer => initializer.Dependencies.All(initializedServices.Contains))
                    .ToArray();

                if (ready.Length == 0)
                {
                    var dependencyGraph = pending.ToDictionary(
                        kvp => kvp.Key,
                        kvp => (IReadOnlyCollection<Type>)kvp.Value.Dependencies
                            .Where(pending.ContainsKey)
                            .ToArray());

                    var cyclePath = DependencyCycleDetector.DetectCyclePath(dependencyGraph);
                    var unresolved = string.Join(", ", pending.Keys.Select(type => type.Name));
                    var cycleDescription = cyclePath != null
                        ? $"Cycle: {string.Join(" \u2192 ", cyclePath.Select(t => t.Name))}. "
                        : string.Empty;

                    throw new InvalidOperationException(
                        $"Initialization dependency cycle detected in scope {scope}. " +
                        $"{cycleDescription}" +
                        $"Remaining services: {unresolved}");
                }

                foreach (var initializer in ready)
                    progressNotifier.OnServiceStarted(scope, initializer.ServiceType, completedServices, totalServices);

                var uniqueImplementations = ready
                    .GroupBy(initializer => initializer.ImplementationType)
                    .Select(group => group.First())
                    .ToArray();

                var taskMap = uniqueImplementations
                    .Select(initializer => (
                        task: ExecuteInitializerWithHealthAsync(scope, context, initializer, progressNotifier, completedServices, totalServices, cancellationToken),
                        initializer))
                    .ToArray();

                var waveTask = Task.WhenAll(taskMap.Select(t => t.task));
                var waveStallTimeout = _healthSupervisor.Options.WaveStallTimeout;
                if (_healthSupervisor.IsEnabled && waveStallTimeout > TimeSpan.Zero && waveStallTimeout != Timeout.InfiniteTimeSpan)
                {
                    var completedFirst = await Task.WhenAny(waveTask, Task.Delay(waveStallTimeout, cancellationToken)).ConfigureAwait(false);
                    if (completedFirst != waveTask)
                    {
                        var stalledNames = taskMap
                            .Where(t => !t.task.IsCompleted)
                            .Select(t => t.initializer.ServiceType.Name)
                            .ToArray();

                        if (stalledNames.Length > 0)
                        {
                            _logger.LogWarning(
                                "[RuntimeFlow] Wave stall detected in scope {Scope}: {Count} service(s) haven't completed after {Timeout:F0}s: {Services}",
                                scope, stalledNames.Length, waveStallTimeout.TotalSeconds, string.Join(", ", stalledNames));
                        }

                        await waveTask.ConfigureAwait(false);
                    }
                }
                else
                {
                    await waveTask.ConfigureAwait(false);
                }

                ThrowIfStaleGeneration(generation, cancellationToken);
                foreach (var initializer in ready)
                {
                    pending.Remove(initializer.ServiceType);
                    initializedServices.Add(initializer.ServiceType);
                    initOrder.Add(initializer.ServiceType);
                    completedServices++;
                    progressNotifier.OnServiceCompleted(scope, initializer.ServiceType, completedServices, totalServices);
                }
            }

            _scopeInitializationOrder[(scope, scopeKey)] = initOrder;
            return totalServices;
        }

        private async Task ExecuteInitializerWithHealthAsync(
            GameContextType scope,
            GameContext context,
            ServiceInitializerBinding initializer,
            IInitializationProgressNotifier progressNotifier,
            int completedServices,
            int totalServices,
            CancellationToken cancellationToken)
        {
            var resolved = context.Resolve(initializer.ServiceType);
            if (resolved is not IAsyncInitializableService asyncService)
            {
                throw new InvalidOperationException(
                    $"Service {initializer.ServiceType.Name} is expected to implement {nameof(IAsyncInitializableService)}.");
            }

            var affinity = resolved is IInitializationThreadAffinityProvider affinityProvider
                ? affinityProvider.ThreadAffinity
                : InitializationThreadAffinity.MainThread;

            var timeout = _healthSupervisor.GetServiceTimeout(scope, initializer.ServiceType);
            var stopwatch = Stopwatch.StartNew();
            using var serviceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_healthSupervisor.IsEnabled && timeout != Timeout.InfiniteTimeSpan)
            {
                serviceCts.CancelAfter(timeout);
            }

            try
            {
                if (resolved is IProgressAwareInitializableService progressAware)
                {
                    var initContext = new ServiceInitializationContext(scope, initializer.ServiceType, progressNotifier, completedServices, totalServices);
                    await _executionScheduler.ExecuteAsync(
                            affinity,
                            async token => await progressAware.InitializeAsync(initContext, token).ConfigureAwait(false),
                            serviceCts.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _executionScheduler.ExecuteAsync(
                            affinity,
                            async token => await asyncService.InitializeAsync(token).ConfigureAwait(false),
                            serviceCts.Token)
                        .ConfigureAwait(false);
                }

                stopwatch.Stop();
                _logger.LogDebug("Service {ServiceType} initialized ({Duration}ms)", initializer.ServiceType.Name, stopwatch.Elapsed.TotalMilliseconds.ToString("F1"));
                _healthSupervisor.RecordServiceSuccess(scope, initializer.ServiceType, stopwatch.Elapsed, timeout);
            }
            catch (OperationCanceledException ex)
                when (_healthSupervisor.IsEnabled
                      && serviceCts.IsCancellationRequested
                      && !cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                _logger.LogWarning("Service {ServiceType} init slow ({Duration}ms, baseline {Baseline}ms)", initializer.ServiceType.Name, stopwatch.Elapsed.TotalMilliseconds.ToString("F1"), timeout.TotalMilliseconds.ToString("F1"));
                var critical = new RuntimeHealthCriticalException(
                    scope,
                    initializer.ServiceType,
                    timeout,
                    stopwatch.Elapsed,
                    ex);
                _healthSupervisor.RecordServiceFailure(
                    scope,
                    initializer.ServiceType,
                    stopwatch.Elapsed,
                    timeout,
                    critical);
                throw critical;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "Service {ServiceType} initialization failed", initializer.ServiceType.Name);
                _healthSupervisor.RecordServiceFailure(
                    scope,
                    initializer.ServiceType,
                    stopwatch.Elapsed,
                    timeout,
                    ex);
                throw;
            }
        }

        private void ValidateDependencies(
            GameContextType scope,
            GameContext context,
            IReadOnlyDictionary<Type, ServiceInitializerBinding> pending,
            ISet<Type> initializedServices)
        {
            foreach (var initializer in pending.Values)
            {
                foreach (var dependency in initializer.Dependencies)
                {
                    if (!pending.ContainsKey(dependency)
                        && !initializedServices.Contains(dependency)
                        && !IsDependencyAvailableInParent(context.Parent, dependency))
                    {
                        throw new InvalidOperationException(
                            $"Initializer for {initializer.ServiceType.Name} depends on {dependency.Name}, but this dependency was not initialized before scope {scope}.");
                    }
                }
            }
        }

        private static bool IsDependencyAvailableInParent(IGameContext? parent, Type dependency)
        {
            if (parent == null || dependency == null)
                return false;

            if (parent.IsRegistered(dependency))
                return true;

            if (parent is GameContext gameContext)
                return IsDependencyAvailableInParent(gameContext.Parent, dependency);

            return false;
        }

        private static List<ServiceInitializerBinding> DiscoverInitializers(GameContext context)
        {
            var compiledGraph = RuntimeFlowCompiledInitializationGraph.RuleVersion == InitializationGraphRules.Version
                ? RuntimeFlowCompiledInitializationGraph.Nodes
                    .GroupBy(node => node.ServiceType)
                    .ToDictionary(group => group.Key, group => group.Last())
                : new Dictionary<Type, RuntimeFlowCompiledInitializationGraph.Node>();

            var candidateServiceTypes = new HashSet<Type>(
                context.RegisteredServiceTypes.Where(InitializationGraphRules.IsExplicitDependencyType));

            if (candidateServiceTypes.Count == 0)
            {
                foreach (var discoveredType in DiscoverRegisteredAsyncServiceTypes(context))
                {
                    candidateServiceTypes.Add(discoveredType);
                }
            }

            foreach (var node in compiledGraph.Values)
            {
                if (InitializationGraphRules.IsAsyncDependencyType(node.ServiceType)
                    && context.IsRegistered(node.ServiceType))
                {
                    candidateServiceTypes.Add(node.ServiceType);
                }
            }

            // Phase 1: Build raw bindings with implementation types resolved.
            // Deduplicate by implementation type to avoid initializing the same singleton twice
            // when it's registered as both self and interface(s).
            var rawBindingsByImplementation = new Dictionary<Type, (Type serviceType, Type implementationType, IReadOnlyCollection<Type> rawDependencies)>();
            foreach (var serviceType in candidateServiceTypes.OrderBy(GetDeterministicTypeName, StringComparer.Ordinal))
            {
                Type implementationType;
                IReadOnlyCollection<Type> rawDependencies;

                if (compiledGraph.TryGetValue(serviceType, out var compiledNode))
                {
                    implementationType = compiledNode.ImplementationType;
                    rawDependencies = compiledNode.Dependencies
                        .Where(InitializationGraphRules.IsExplicitDependencyType)
                        .Distinct()
                        .ToArray();
                }
                else
                {
                    if (!context.TryGetImplementationType(serviceType, out implementationType))
                    {
                        // Avoid eager resolve while only building dependency graph.
                        // For concrete registrations, the service type itself is the implementation type.
                        if (serviceType.IsInterface)
                            continue;

                        implementationType = serviceType;
                    }

                    rawDependencies = InitializationGraphRules.ResolveConstructorDependencies(implementationType);
                }

                if (rawBindingsByImplementation.TryGetValue(implementationType, out var existingBinding))
                {
                    var mergedDependencies = existingBinding.rawDependencies
                        .Concat(rawDependencies)
                        .Distinct()
                        .ToArray();

                    var preferredServiceType = IsPreferredServiceType(
                        serviceType,
                        existingBinding.serviceType,
                        implementationType)
                        ? serviceType
                        : existingBinding.serviceType;

                    rawBindingsByImplementation[implementationType] =
                        (preferredServiceType, implementationType, mergedDependencies);
                    continue;
                }

                rawBindingsByImplementation[implementationType] =
                    (serviceType, implementationType, rawDependencies);
            }

            var rawBindings = rawBindingsByImplementation.Values.ToArray();

            // Phase 2: Build type alias mapping to canonical service type for the initializer graph.
            // This correctly handles concrete classes referenced via [DependsOn] and
            // interface aliases that point to the same implementation.
            var typeToServiceType = new Dictionary<Type, Type>();
            foreach (var (serviceType, implementationType, _) in rawBindings)
            {
                typeToServiceType[implementationType] = serviceType;
                typeToServiceType[serviceType] = serviceType;
            }

            foreach (var candidateServiceType in candidateServiceTypes)
            {
                if (!context.TryGetImplementationType(candidateServiceType, out var implementationType))
                    continue;

                if (typeToServiceType.TryGetValue(implementationType, out var canonicalServiceType))
                {
                    typeToServiceType[candidateServiceType] = canonicalServiceType;
                }
            }

            // Phase 3: Resolve raw dependencies to service types (graph keys).
            var initializers = new List<ServiceInitializerBinding>();
            foreach (var (serviceType, implementationType, rawDependencies) in rawBindings)
            {
                var resolvedDependencies = rawDependencies
                    .Select(dep => ResolveToServiceType(dep, typeToServiceType, candidateServiceTypes))
                    .Where(dep => dep != null)
                    .Select(dep => dep!)
                    .Distinct()
                    .ToArray();

                initializers.Add(new ServiceInitializerBinding(serviceType, implementationType, resolvedDependencies));
            }

            return initializers;
        }

        private static IEnumerable<Type> DiscoverRegisteredAsyncServiceTypes(GameContext context)
        {
            foreach (var type in ExplicitDependencyTypeCatalog.Value)
            {
                if (context.IsRegistered(type))
                    yield return type;
            }
        }

        private static Type[] BuildExplicitDependencyTypeCatalog()
        {
            var result = new HashSet<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types.Where(type => type != null).Cast<Type>().ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (InitializationGraphRules.IsExplicitDependencyType(type))
                        result.Add(type);
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// Resolves a dependency type to its service type in the initialization graph.
        /// If <paramref name="dep"/> is an interface present in the candidate set, returns it as-is.
        /// If it's a concrete class, looks up the service type registered with that implementation.
        /// </summary>
        private static Type? ResolveToServiceType(
            Type dep,
            Dictionary<Type, Type> typeToServiceType,
            HashSet<Type> candidateServiceTypes)
        {
            if (typeToServiceType.TryGetValue(dep, out var mappedServiceType))
                return mappedServiceType;

            // Interface that is a known graph key — use directly.
            if (dep.IsInterface && candidateServiceTypes.Contains(dep))
                return dep;

            // Interface not in the graph (e.g., removed marker) — try as impl type.
            if (dep.IsInterface)
                return null;

            return null;
        }

        private static bool IsPreferredServiceType(Type candidate, Type current, Type implementationType)
        {
            var candidateIsImplementation = candidate == implementationType;
            var currentIsImplementation = current == implementationType;
            if (candidateIsImplementation != currentIsImplementation)
                return candidateIsImplementation;

            if (candidate.IsInterface != current.IsInterface)
                return !candidate.IsInterface;

            return string.Compare(
                       GetDeterministicTypeName(candidate),
                       GetDeterministicTypeName(current),
                       StringComparison.Ordinal) < 0;
        }

        private ScopeActivationExecutionPlan DiscoverScopeActivationExecutionPlan(
            GameContextType scope,
            GameContext context)
        {
            var markerType = ResolveScopeActivationMarker(scope);
            var participants = new List<ScopeActivationParticipantBinding>();

            foreach (var serviceType in context.RegisteredServiceTypes.Distinct())
            {
                Type implementationType;
                if (!context.TryGetImplementationType(serviceType, out implementationType))
                {
                    if (serviceType.IsInterface)
                        continue;

                    implementationType = serviceType;
                }

                if (!markerType.IsAssignableFrom(serviceType) && !markerType.IsAssignableFrom(implementationType))
                    continue;

                participants.Add(new ScopeActivationParticipantBinding(serviceType, implementationType));
            }

            var ordered = participants
                .GroupBy(participant => participant.ImplementationType)
                .Select(group => group
                    .OrderBy(participant => GetDeterministicTypeName(participant.ServiceType), StringComparer.Ordinal)
                    .First())
                .OrderBy(participant => GetDeterministicTypeName(participant.ImplementationType), StringComparer.Ordinal)
                .ThenBy(participant => GetDeterministicTypeName(participant.ServiceType), StringComparer.Ordinal)
                .ToArray();

            return new ScopeActivationExecutionPlan(ordered);
        }

        private Task ExecuteScopeActivationEnterAsync(
            GameContextType scope,
            GameContext context,
            CancellationToken cancellationToken)
        {
            return ExecuteScopeActivationEnterAsync(
                scope,
                context,
                NullInitializationProgressNotifier.Instance,
                totalServices: 0,
                cancellationToken);
        }

        private Task ExecuteScopeActivationEnterAsync(
            GameContextType scope,
            GameContext context,
            IInitializationProgressNotifier progressNotifier,
            int totalServices,
            CancellationToken cancellationToken)
        {
            var executionPlan = DiscoverScopeActivationExecutionPlan(scope, context);
            return ExecuteScopeActivationEnterAsync(scope, context, executionPlan, progressNotifier, totalServices, cancellationToken);
        }

        private Task ExecuteScopeActivationExitAsync(
            GameContextType scope,
            GameContext context,
            CancellationToken cancellationToken)
        {
            return ExecuteScopeActivationExitAsync(
                scope,
                context,
                NullInitializationProgressNotifier.Instance,
                cancellationToken);
        }

        private Task ExecuteScopeActivationExitAsync(
            GameContextType scope,
            GameContext context,
            IInitializationProgressNotifier progressNotifier,
            CancellationToken cancellationToken)
        {
            var executionPlan = DiscoverScopeActivationExecutionPlan(scope, context);
            return ExecuteScopeActivationExitAsync(scope, context, executionPlan, progressNotifier, cancellationToken);
        }

        private Task ExecuteScopeActivationEnterAsync(
            GameContextType scope,
            GameContext context,
            ScopeActivationExecutionPlan executionPlan,
            IInitializationProgressNotifier progressNotifier,
            int totalServices,
            CancellationToken cancellationToken)
        {
            var completedStep = Math.Max(0, totalServices);
            return ExecuteScopeActivationPhaseAsync(
                context,
                executionPlan.EnterOrder,
                static (service, token) => service.OnScopeActivatedAsync(token),
                onPhaseStarted: () => NotifyScopeActivationStarted(progressNotifier, scope, completedStep),
                onPhaseCompleted: () => NotifyScopeActivationCompleted(progressNotifier, scope, completedStep),
                cancellationToken);
        }

        private Task ExecuteScopeActivationExitAsync(
            GameContextType scope,
            GameContext context,
            ScopeActivationExecutionPlan executionPlan,
            IInitializationProgressNotifier progressNotifier,
            CancellationToken cancellationToken)
        {
            return ExecuteScopeActivationPhaseAsync(
                context,
                executionPlan.ExitOrder,
                static (service, token) => service.OnScopeDeactivatingAsync(token),
                onPhaseStarted: () => NotifyScopeDeactivationStarted(progressNotifier, scope),
                onPhaseCompleted: () => NotifyScopeDeactivationCompleted(progressNotifier, scope),
                cancellationToken);
        }

        private async Task ExecuteScopeActivationPhaseAsync(
            GameContext context,
            IReadOnlyList<ScopeActivationParticipantBinding> participants,
            Func<IAsyncScopeActivationService, CancellationToken, Task> callback,
            Action? onPhaseStarted,
            Action? onPhaseCompleted,
            CancellationToken cancellationToken)
        {
            onPhaseStarted?.Invoke();
            foreach (var participant in participants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolved = context.Resolve(participant.ServiceType);
                if (resolved is not IAsyncScopeActivationService activationService)
                {
                    throw new InvalidOperationException(
                        $"Service {participant.ServiceType.Name} is expected to implement {nameof(IAsyncScopeActivationService)}.");
                }

                var affinity = resolved is IInitializationThreadAffinityProvider affinityProvider
                    ? affinityProvider.ThreadAffinity
                    : InitializationThreadAffinity.MainThread;

                await _executionScheduler.ExecuteAsync(
                        affinity,
                        token => callback(activationService, token),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            onPhaseCompleted?.Invoke();
        }

        private static void NotifyScopeActivationStarted(
            IInitializationProgressNotifier progressNotifier,
            GameContextType scope,
            int totalServices)
        {
            if (progressNotifier is IRuntimeScopeLifecycleProgressNotifier lifecycleProgressNotifier)
                lifecycleProgressNotifier.OnScopeActivationStarted(scope, totalServices, totalServices);
        }

        private static void NotifyScopeActivationCompleted(
            IInitializationProgressNotifier progressNotifier,
            GameContextType scope,
            int totalServices)
        {
            if (progressNotifier is IRuntimeScopeLifecycleProgressNotifier lifecycleProgressNotifier)
                lifecycleProgressNotifier.OnScopeActivationCompleted(scope, totalServices, totalServices);
        }

        private static void NotifyScopeDeactivationStarted(
            IInitializationProgressNotifier progressNotifier,
            GameContextType scope)
        {
            if (progressNotifier is IRuntimeScopeLifecycleProgressNotifier lifecycleProgressNotifier)
                lifecycleProgressNotifier.OnScopeDeactivationStarted(scope);
        }

        private static void NotifyScopeDeactivationCompleted(
            IInitializationProgressNotifier progressNotifier,
            GameContextType scope)
        {
            if (progressNotifier is IRuntimeScopeLifecycleProgressNotifier lifecycleProgressNotifier)
                lifecycleProgressNotifier.OnScopeDeactivationCompleted(scope);
        }

        private static Type ResolveScopeActivationMarker(GameContextType scope)
        {
            return scope switch
            {
                GameContextType.Session => typeof(ISessionScopeActivationService),
                GameContextType.Scene => typeof(ISceneScopeActivationService),
                GameContextType.Module => typeof(IModuleScopeActivationService),
                _ => throw new InvalidOperationException(
                    $"Scope activation hooks are available only for Session/Scene/Module scopes. Requested scope: {scope}.")
            };
        }

        private static string GetDeterministicTypeName(Type type)
        {
            return type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
        }

        private async Task CancelActiveLoadAsync(CancellationToken cancellationToken = default)
        {
            if (_activeLoadCts == null)
                return;

            _activeLoadCts.Cancel();
            try
            {
                await AwaitWithCancellation(_activeLoadTask, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception) when (_activeLoadCts.IsCancellationRequested)
            {
            }
            finally
            {
                _activeLoadCts.Dispose();
                _activeLoadCts = null;
                _activeLoadTask = Task.CompletedTask;
            }
        }

        internal async Task DisposeAllScopesAsync(CancellationToken cancellationToken = default)
        {
            await CancelActiveLoadAsync(cancellationToken).ConfigureAwait(false);

            await DisposePreloadedContextsAsync(cancellationToken).ConfigureAwait(false);
            await DisposeAdditiveModulesAsync(cancellationToken).ConfigureAwait(false);

            if (_moduleContext != null)
            {
                SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Deactivating, _activeModuleScopeKey);
                await ExecuteScopeActivationExitAsync(GameContextType.Module, _moduleContext, cancellationToken).ConfigureAwait(false);
                await DisposeScopeContextAsync(
                        GameContextType.Module,
                        _moduleContext,
                        cancellationToken,
                        _activeModuleScopeKey,
                        () => SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Disposed, _activeModuleScopeKey))
                    .ConfigureAwait(false);
                _moduleContext = null;
                _logger.LogDebug("Scope {Scope} disposed", GameContextType.Module);
            }

            if (_sceneContext != null)
            {
                SetScopeStateIfTracked(GameContextType.Scene, ScopeLifecycleState.Deactivating, _activeSceneScopeKey);
                await ExecuteScopeActivationExitAsync(GameContextType.Scene, _sceneContext, cancellationToken).ConfigureAwait(false);
                await DisposeScopeContextAsync(
                        GameContextType.Scene,
                        _sceneContext,
                        cancellationToken,
                        _activeSceneScopeKey,
                        () => SetScopeStateIfTracked(GameContextType.Scene, ScopeLifecycleState.Disposed, _activeSceneScopeKey))
                    .ConfigureAwait(false);
                _sceneContext = null;
                _logger.LogDebug("Scope {Scope} disposed", GameContextType.Scene);
            }

            if (_sessionContext != null)
            {
                SetScopeStateIfTracked(GameContextType.Session, ScopeLifecycleState.Deactivating);
                await ExecuteScopeActivationExitAsync(GameContextType.Session, _sessionContext, cancellationToken).ConfigureAwait(false);
                await DisposeScopeContextAsync(
                        GameContextType.Session,
                        _sessionContext,
                        cancellationToken,
                        onDisposed: () => SetScopeStateIfTracked(GameContextType.Session, ScopeLifecycleState.Disposed))
                    .ConfigureAwait(false);
                _sessionContext = null;
                _logger.LogDebug("Scope {Scope} disposed", GameContextType.Session);
            }

            if (_ownsGlobalContext)
            {
                await DisposeContextAsync(_globalContext, cancellationToken).ConfigureAwait(false);
                _globalContext = null;
                _logger.LogDebug("Scope {Scope} disposed", GameContextType.Global);
            }
        }

        private async Task DisposePreloadedContextsAsync(CancellationToken cancellationToken)
        {
            foreach (var kvp in _preloadedContexts.ToArray())
            {
                var scopeType = _declaredScopeTypes.TryGetValue(kvp.Key, out var declaredScopeType)
                    ? declaredScopeType
                    : GameContextType.Scene;

                if (scopeType is GameContextType.Scene or GameContextType.Module)
                {
                    SetScopeStateIfTracked(scopeType, ScopeLifecycleState.Deactivating, kvp.Key);
                    await DisposeScopeContextAsync(
                            scopeType,
                            kvp.Value,
                            cancellationToken,
                            kvp.Key,
                            () => SetScopeStateIfTracked(scopeType, ScopeLifecycleState.Disposed, kvp.Key))
                        .ConfigureAwait(false);
                }
                else
                {
                    await DisposeContextAsync(kvp.Value, cancellationToken).ConfigureAwait(false);
                }
            }

            _preloadedContexts.Clear();
        }

        private async Task DisposeAdditiveModuleContextsAsync(CancellationToken cancellationToken)
        {
            foreach (var kvp in _additiveModuleContexts)
            {
                await DisposeScopeContextAsync(GameContextType.Module, kvp.Value, cancellationToken, kvp.Key).ConfigureAwait(false);
            }

            _additiveModuleContexts.Clear();
        }

        private async Task DisposeAdditiveModulesAsync(CancellationToken cancellationToken)
        {
            foreach (var kvp in _additiveModuleContexts)
            {
                SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Deactivating, kvp.Key);
                await ExecuteScopeActivationExitAsync(GameContextType.Module, kvp.Value, NullInitializationProgressNotifier.Instance, cancellationToken).ConfigureAwait(false);
                await DisposeScopeContextAsync(
                        GameContextType.Module,
                        kvp.Value,
                        cancellationToken,
                        kvp.Key,
                        () => SetScopeStateIfTracked(GameContextType.Module, ScopeLifecycleState.Disposed, kvp.Key))
                    .ConfigureAwait(false);
            }
            _additiveModuleContexts.Clear();
        }

        private static async Task AwaitWithCancellation(Task task, CancellationToken cancellationToken)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));

            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return;
            }

            var cancellationTask = Task.Delay(Timeout.Infinite, cancellationToken);
            var completed = await Task.WhenAny(task, cancellationTask).ConfigureAwait(false);
            if (completed != task)
                cancellationToken.ThrowIfCancellationRequested();

            await task.ConfigureAwait(false);
        }

        private void ThrowIfStaleGeneration(long generation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _runGeneration))
                throw new OperationCanceledException(cancellationToken);
        }

    }
}
