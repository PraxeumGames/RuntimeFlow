using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using VContainer;
using VContainer.Diagnostics;
using VContainer.Internal;

namespace RuntimeFlow.Contexts
{
    internal sealed class GameContextCore : IObjectResolver, IAsyncDisposable
    {
        private readonly IGameContext? _parent;
        private readonly GameContextRegistrationStore _registrationStore = new();
        private readonly GameContextDecorationChain _decorationChain = new();
        private readonly GameContextInstanceLedger _instances = new();
        private readonly List<ServiceInitializerBinding> _initializationOrder = new();
        private readonly HashSet<Type> _initializationOrderLookup = new();
        private Registry? _registry;
        private volatile bool _initialized;
        private volatile bool _disposed;
        private static int _contextInstanceCounter;
        private DiagnosticsCollector? _diagnostics;

        public event Action? OnBeforeInitialize;
        public event Action? OnInitialized;
        public event Action? OnBeforeDispose;
        public event Action? OnDisposed;

        internal IInitializationExecutionScheduler? ExecutionScheduler { get; set; }
        internal IReadOnlyList<ServiceInitializerBinding> InitializationOrder => _initializationOrder;
        internal IReadOnlyCollection<Type> RegisteredServiceTypes => _registrationStore.RegisteredServiceTypes;
        public IGameContext? Parent => _parent;
        public bool IsInitialized => _initialized;
        public bool IsDisposed => _disposed;

        public DiagnosticsCollector Diagnostics
        {
            get => _diagnostics ??= CreateDiagnosticsCollector();
            set => _diagnostics = value;
        }

        private static DiagnosticsCollector CreateDiagnosticsCollector()
        {
            var id = System.Threading.Interlocked.Increment(ref _contextInstanceCounter);
            return new DiagnosticsCollector($"GameContext-{id}");
        }

        public object ApplicationOrigin => _ownerResolver ?? (object)this;
        private IObjectResolver? _ownerResolver;

        internal void SetOwnerResolver(IObjectResolver resolver) => _ownerResolver = resolver;

        public IObjectResolver Resolver => _initialized
            ? (_ownerResolver ?? this)
            : throw new InvalidOperationException("Context not initialized");

        public GameContextCore(IGameContext? parent = null)
        {
            _parent = parent;
        }

        internal void RecordInitialized(ServiceInitializerBinding initializer)
        {
            if (initializer == null) throw new ArgumentNullException(nameof(initializer));
            if (_initializationOrderLookup.Add(initializer.ServiceType))
                _initializationOrder.Add(initializer);
        }

        public void Register<TService, TImplementation>() where TImplementation : TService
        {
            var serviceType = typeof(TService);
            var implType = typeof(TImplementation);
            _registrationStore.Register(serviceType, implType, DiLifetimeMapper.ToVContainer(DiLifetime.Singleton));
        }

        public void Register(Type serviceType, Type implementationType)
        {
            Register(serviceType, implementationType, DiLifetime.Singleton);
        }

        public void Register(Type serviceType, Type implementationType, DiLifetime lifetime)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (implementationType == null) throw new ArgumentNullException(nameof(implementationType));
            if (!serviceType.IsAssignableFrom(implementationType) && serviceType != implementationType)
                throw new InvalidOperationException($"Service type {serviceType.Name} is not assignable from {implementationType.Name}.");
            _registrationStore.Register(serviceType, implementationType, DiLifetimeMapper.ToVContainer(lifetime));
        }

        public void ConfigureContainer(Action<IContainerBuilder> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            _registrationStore.ConfigureContainer(configure);
        }

        public void Decorate(Type serviceType, Type decoratorType)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (decoratorType == null) throw new ArgumentNullException(nameof(decoratorType));
            _decorationChain.Add(serviceType, decoratorType);
        }

        public bool IsRegistered(Type serviceType, bool includeInterfaceTypes = true)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (_registrationStore.IsRegistered(serviceType, includeInterfaceTypes))
                return true;
            if (_initialized && _registry != null && _registry.TryGet(serviceType, out var registration))
            {
                if (includeInterfaceTypes || registration!.ImplementationType == serviceType)
                    return true;
            }
            return false;
        }

        public void RegisterInstance(Type serviceType, object instance)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            RegisterInstanceEx(instance.GetType(), instance, new[] { serviceType }, ownsLifetime: true);
        }

        public void RegisterInstance<TService>(TService instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            RegisterInstanceEx(instance.GetType(), instance, new[] { typeof(TService) }, ownsLifetime: true);
        }

        public void RegisterInstance(object instance, IReadOnlyCollection<Type> serviceTypes)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (serviceTypes == null) throw new ArgumentNullException(nameof(serviceTypes));
            RegisterInstanceEx(instance.GetType(), instance, serviceTypes, ownsLifetime: true);
        }

        internal void RegisterImportedInstance(object instance, IReadOnlyCollection<Type> serviceTypes)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (serviceTypes == null) throw new ArgumentNullException(nameof(serviceTypes));
            RegisterInstanceEx(instance.GetType(), instance, serviceTypes, ownsLifetime: false);
        }

        public TService Resolve<TService>() => (TService)Resolve(typeof(TService));

        public object Resolve(Type serviceType)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (!_initialized || _registry == null) throw new InvalidOperationException("Context not initialized");
            GameContextThreadDispatcher.EnsureMainThreadOperationAllowed($"resolve '{serviceType.FullName}'");
            return ResolveCore(serviceType);
        }

        public async System.Threading.Tasks.Task<TService> ResolveAsync<TService>(System.Threading.CancellationToken cancellationToken = default)
        {
            var resolved = await ResolveAsync(typeof(TService), cancellationToken).ConfigureAwait(false);
            return (TService)resolved;
        }

        public System.Threading.Tasks.Task<object> ResolveAsync(Type serviceType, System.Threading.CancellationToken cancellationToken = default)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (!_initialized || _registry == null) throw new InvalidOperationException("Context not initialized");
            return GameContextThreadDispatcher.DispatchToMainThreadAsync(() => ResolveCore(serviceType), $"resolve '{serviceType.FullName}'", cancellationToken);
        }

        public System.Threading.Tasks.Task<object> ResolveAsync(Registration registration, System.Threading.CancellationToken cancellationToken = default)
        {
            if (registration == null) throw new ArgumentNullException(nameof(registration));
            if (!_initialized || _registry == null) throw new InvalidOperationException("Context not initialized");
            var description = $"resolve '{registration.ImplementationType?.FullName ?? registration.ImplementationType?.Name ?? "<unknown>"}'";
            return GameContextThreadDispatcher.DispatchToMainThreadAsync(() => ResolveRegistration(registration), description, cancellationToken);
        }

        private object ResolveCore(Type serviceType)
        {
            if (_decorationChain.HasDecorationsFor(serviceType))
                return _decorationChain.GetOrMaterializeDecorated(serviceType, this, ResolveUndecorated);
            if (_registry!.TryGet(serviceType, out var registration) && registration != null)
                return ResolveRegistration(registration);
            if (_parent != null)
                return _parent.Resolve(serviceType);
            throw new VContainerException(serviceType, $"No such registration of type: {serviceType}");
        }

        private object ResolveUndecorated(Type serviceType)
        {
            if (_registry!.TryGet(serviceType, out var registration) && registration != null)
                return ResolveRegistration(registration);
            throw new VContainerException(serviceType, $"No such registration of type: {serviceType}");
        }

        internal bool TryGetImplementationType(Type serviceType, [MaybeNullWhen(false)] out Type implementationType)
        {
            if (_registrationStore.TryGetImplementationType(serviceType, out implementationType))
                return true;
            if (_initialized && _registry != null && _registry.TryGet(serviceType, out var registration) && registration != null)
            {
                implementationType = registration.ImplementationType;
                return true;
            }
            implementationType = null;
            return false;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Type> GenericListTypeCache = new();

        internal IReadOnlyList<Registration> GetRegistrationsForServiceType(Type serviceType)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (_registry == null)
                return Array.Empty<Registration>();

            var registrations = new List<Registration>();
            var collectionType = GenericListTypeCache.GetOrAdd(serviceType, static t => typeof(IReadOnlyList<>).MakeGenericType(t));
            if (_registry.TryGet(collectionType, out var collectionRegistration)
                && collectionRegistration?.Provider is IEnumerable collectionProvider)
            {
                foreach (var item in collectionProvider)
                {
                    if (item is Registration reg)
                        registrations.Add(reg);
                }
            }
            if (_registry.TryGet(serviceType, out var registration) && registration != null)
                registrations.Add(registration);

            if (registrations.Count <= 1)
                return registrations;

            var result = new List<Registration>(registrations.Count);
            var seenImpls = new HashSet<Type>();
            for (var i = 0; i < registrations.Count; i++)
            {
                var reg = registrations[i];
                if (reg.ImplementationType != null && seenImpls.Add(reg.ImplementationType))
                    result.Add(reg);
            }
            return result;
        }

        internal bool TryGetRegisteredInstance(Type serviceType, [MaybeNullWhen(false)] out object instance)
        {
            return _registrationStore.TryGetRegisteredInstance(serviceType, out instance);
        }

        /// <summary>
        /// Ledger read of an already-initialized instance without construction or dispatch.
        /// Used for cross-context dependency reads (auto-service parent fallbacks) that must
        /// not block against the main thread.
        /// </summary>
        internal bool TryGetInitializedByType(Type serviceType, [MaybeNullWhen(false)] out object instance)
        {
            instance = null!;
            if (_registry != null && _registry.TryGet(serviceType, out var registration)
                && registration != null
                && _instances.TryGetShared(registration, out instance!))
                return true;
            return false;
        }

        internal void RegisterInstanceEx(Type implementationType, object instance, IReadOnlyCollection<Type> serviceTypes, bool ownsLifetime)
        {
            _registrationStore.RegisterInstance(implementationType, instance, serviceTypes);
            if (ownsLifetime)
                _instances.TrackOwned(instance);
        }

        public void Initialize()
        {
            if (_initialized) return;
            // Thread-agnostic by design: InitializeCore only builds the registration graph
            // (pure C#); Unity-bound construction happens at Resolve time, which enforces
            // its own main-thread contract.
            InitializeCore();
        }

        private void InitializeCore()
        {
            _disposed = false;
            OnBeforeInitialize?.Invoke();
            _decorationChain.ValidateRegistrations(serviceType => IsRegistered(serviceType));
            _initialized = true;
            try
            {
                var builder = new RuntimeFlowContainerBuilder();
                _registrationStore.ApplyRegistrations(builder);
                _registry = builder.BuildRegistry(this, Diagnostics);
                Diagnostics.NotifyContainerBuilt(this);
            }
            catch
            {
                _initialized = false;
                _registry = null;
                _instances.ClearShared();
                throw;
            }
            OnInitialized?.Invoke();
        }

        public object Resolve(Registration registration)
        {
            if (registration == null) throw new ArgumentNullException(nameof(registration));
            if (!_initialized || _registry == null) throw new InvalidOperationException("Context not initialized");
            GameContextThreadDispatcher.EnsureMainThreadOperationAllowed(
                $"resolve '{registration.ImplementationType?.FullName ?? registration.ImplementationType?.Name ?? "<unknown>"}'");
            return ResolveRegistration(registration);
        }

        public bool TryResolve(Type serviceType, out object resolved)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (!_initialized || _registry == null)
            {
                resolved = null!;
                return false;
            }
            if (!TryFindRegistrationAcrossChain(serviceType, out _))
            {
                resolved = null!;
                return false;
            }
            resolved = Resolve(serviceType);
            return true;
        }

        public void Inject(object instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (!_initialized || _registry == null) throw new InvalidOperationException("Context not initialized");
            InjectorCache.GetOrBuild(instance.GetType()).Inject(instance, this, null);
        }

        public bool TryGetRegistration(Type type, out Registration registration)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (_registry != null && _registry.TryGet(type, out registration))
                return true;
            registration = null!;
            return false;
        }

        IScopedObjectResolver IObjectResolver.CreateScope(Action<IContainerBuilder> installation)
        {
            throw new NotSupportedException("GameContext owns scope lifecycle; scopes are created by GameContextBuilder and activated through the RuntimeFlow pipeline.");
        }

        private object ResolveRegistration(Registration registration) => _diagnostics.TraceResolve(registration, ResolveRegistrationCore);

        private object ResolveRegistrationCore(Registration registration)
        {
            switch (registration.Lifetime)
            {
                case Lifetime.Singleton:
                case Lifetime.Scoped:
                    return GetOrCreateSharedInstance(registration);
                default:
                    return registration.SpawnInstance(this);
            }
        }

        private object GetOrCreateSharedInstance(Registration registration)
        {
            if (_instances.TryGetShared(registration, out var existing))
                return existing;
            var instance = registration.SpawnInstance(this);
            _instances.AddShared(registration, instance);
            if (registration.Provider is not FixedInstanceProvider)
                _instances.TrackOwned(instance);
            return instance;
        }

        private bool TryFindRegistrationAcrossChain(Type serviceType, out Registration registration)
        {
            GameContextCore? current = this;
            while (current != null)
            {
                if (current._registry != null && current._registry.TryGet(serviceType, out registration))
                    return true;
                if (current._parent is GameContext gc)
                    current = gc.Core;
                else
                    current = null;
            }
            registration = null!;
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (HasPendingAsyncDisposals())
                throw new NotSupportedException("GameContext owns asynchronous services; use await DisposeAsync() instead of Dispose() so their disposal runs on the correct scheduler and thread affinity.");
            _disposed = true;
            var failures = new List<Exception>();
            RunSynchronousTeardown(failures);
            if (failures.Count > 0)
                throw new AggregateException("GameContext disposal encountered one or more failures.", failures);
        }

        public async System.Threading.Tasks.ValueTask DisposeAsync(System.Threading.CancellationToken cancellationToken = default)
        {
            if (ExecutionScheduler == null && HasPendingAsyncDisposals())
                throw new InvalidOperationException("An ExecutionScheduler is required to dispose async-disposable services. Create the context through GameContextBuilder or set ExecutionScheduler explicitly.");
            if (_disposed) return;
            _disposed = true;
            var failures = new List<Exception>();
            await DisposeInitializedServicesAsync(cancellationToken, failures).ConfigureAwait(false);
            await RunSynchronousTeardownAsync(failures, cancellationToken).ConfigureAwait(false);
            if (failures.Count > 0)
                throw new AggregateException("GameContext disposal encountered one or more failures.", failures);
        }

        System.Threading.Tasks.ValueTask IAsyncDisposable.DisposeAsync() => DisposeAsync();

        private async System.Threading.Tasks.Task DisposeInitializedServicesAsync(System.Threading.CancellationToken cancellationToken, List<Exception> failures)
        {
            if (_initializationOrder.Count == 0) return;
            var scheduler = ExecutionScheduler ?? InlineInitializationExecutionScheduler.Instance;
            var disposedInstances = new HashSet<object>(ReferenceEqualityComparer.Instance);
            for (var i = _initializationOrder.Count - 1; i >= 0; i--)
            {
                var initializer = _initializationOrder[i];
                if (!TryGetInitializedInstance(initializer, out var instance)) continue;
                if (!disposedInstances.Add(instance)) continue;
                try
                {
                    if (instance is IAsyncDisposableService asyncService)
                    {
                        await scheduler.ExecuteAsync(ResolveTeardownAffinity(instance), token => asyncService.DisposeAsync(token), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (instance is IAsyncDisposable asyncDisposable)
                    {
                        await scheduler.ExecuteAsync(ResolveTeardownAffinity(instance), _ => asyncDisposable.DisposeAsync().AsTask(), cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) { failures.Add(ex); }
            }
        }

        private static InitializationThreadAffinity ResolveTeardownAffinity(object instance)
        {
            return instance is IInitializationThreadAffinityProvider provider ? provider.ThreadAffinity : InitializationThreadAffinity.MainThread;
        }

        internal bool TryGetInitializedInstance(ServiceInitializerBinding initializer, out object instance)
        {
            return _instances.TryGetInitialized(
                initializer.Registration,
                initializer.ResolveServiceType,
                serviceType =>
                {
                    if (_registry != null && _registry.TryGet(serviceType, out var found)) return found;
                    return null;
                },
                out instance);
        }

        private bool HasPendingAsyncDisposals()
        {
            foreach (var initializer in _initializationOrder)
            {
                if (TryGetInitializedInstance(initializer, out var instance) && (instance is IAsyncDisposableService || instance is IAsyncDisposable))
                    return true;
            }
            return false;
        }

        private async System.Threading.Tasks.Task RunSynchronousTeardownAsync(List<Exception> failures, System.Threading.CancellationToken cancellationToken)
        {
            var scheduler = ExecutionScheduler ?? InlineInitializationExecutionScheduler.Instance;
            await scheduler.ExecuteAsync(InitializationThreadAffinity.MainThread, _ => { RunSynchronousTeardown(failures); return System.Threading.Tasks.Task.CompletedTask; }, cancellationToken).ConfigureAwait(false);
        }

        private void RunSynchronousTeardown(List<Exception> failures)
        {
            var onDisposed = OnDisposed;
            try { OnBeforeDispose?.Invoke(); } catch (Exception ex) { failures.Add(ex); }
            _decorationChain.ClearResolvedInstances();
            _instances.DisposeOwnedReverse(failures);
            ResetState();
            try { onDisposed?.Invoke(); } catch (Exception ex) { failures.Add(ex); }
        }

        private void ResetState()
        {
            _registry = null;
            _instances.Clear();
            _initializationOrder.Clear();
            _initializationOrderLookup.Clear();
            _initialized = false;
            _registrationStore.ClearRegistrations();
            _decorationChain.Clear();
            OnBeforeInitialize = null;
            OnInitialized = null;
            OnBeforeDispose = null;
            OnDisposed = null;
        }
    }
}
