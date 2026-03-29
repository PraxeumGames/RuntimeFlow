using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using VContainer;
using System.Linq;

namespace RuntimeFlow.Contexts
{
    public class GameContext : IGameContext
    {
        private static SynchronizationContext? _mainThreadContext;
        private static int _mainThreadId;
        private static readonly TimeSpan MainThreadDispatchTimeout = TimeSpan.FromMinutes(2);

        /// <summary>
        /// The main-thread SynchronizationContext captured at startup.
        /// Use for marshaling Unity API calls from background threads.
        /// </summary>
        public static SynchronizationContext? MainThreadContext => _mainThreadContext;

#if UNITY_5_3_OR_NEWER
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
#endif
        private static void CaptureMainThread()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            // SynchronizationContext may not be set up yet during SubsystemRegistration.
            // Capture it here if available; BeforeSceneLoad callback ensures it's set.
            _mainThreadContext = SynchronizationContext.Current;
        }

#if UNITY_5_3_OR_NEWER
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
#endif
        private static void CaptureMainThreadContext()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _mainThreadContext = SynchronizationContext.Current;
        }

        private readonly IGameContext? _parent;
        private readonly List<Action<VContainer.IContainerBuilder>> _registrations = new();
        private readonly HashSet<Type> _registeredServiceTypes = new();
        private readonly ConcurrentDictionary<Type, Type> _implementationTypes = new();
        private readonly Dictionary<Type, object> _registeredInstances = new();
        private readonly Dictionary<Type, (Lifetime lifetime, List<Type> interfaces)> _typedRegistrations = new();
        private readonly Dictionary<Type, (object instance, List<Type> interfaces, bool ownsLifetime)> _instanceRegistrations = new();
        private readonly List<(Type serviceType, Type decoratorType)> _decorations = new();
        private readonly Dictionary<Type, object> _decoratedInstances = new();
        private readonly List<RuntimeFlowInstanceProvider> _instanceProviders = new();
        private VContainer.IObjectResolver? _container;
        private bool _initialized;

        public event Action? OnBeforeInitialize;
        public event Action? OnInitialized;
        public event Action? OnBeforeDispose;
        public event Action? OnDisposed;

        public IObjectResolver Resolver => _container ?? throw new InvalidOperationException("Context not initialized");
        public IGameContext? Parent => _parent;
        internal IReadOnlyCollection<Type> RegisteredServiceTypes => _registeredServiceTypes;

        public GameContext(IGameContext? parent = null)
        {
            _parent = parent;
        }

        public IGameContext CreateChildContext()
        {
            var child = new GameContext(this);
            return child;
        }

        public void Register<TService, TImplementation>() where TImplementation : TService
        {
            var serviceType = typeof(TService);
            var implType = typeof(TImplementation);
            _registeredServiceTypes.Add(serviceType);
            _implementationTypes[serviceType] = implType;
            AddTypedRegistration(implType, Lifetime.Singleton, serviceType);
        }

        public void Register(Type serviceType, Type implementationType)
        {
            Register(serviceType, implementationType, Lifetime.Singleton);
        }

        public void Register(Type serviceType, Type implementationType, Lifetime lifetime)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (implementationType == null) throw new ArgumentNullException(nameof(implementationType));
            if (!serviceType.IsAssignableFrom(implementationType) && serviceType != implementationType)
            {
                throw new InvalidOperationException(
                    $"Service type {serviceType.Name} is not assignable from {implementationType.Name}.");
            }

            _registeredServiceTypes.Add(serviceType);
            _implementationTypes[serviceType] = implementationType;
            AddTypedRegistration(implementationType, lifetime, serviceType);
        }

        public void ConfigureContainer(Action<IContainerBuilder> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            _registrations.Add(configure);
        }

        public void Decorate(Type serviceType, Type decoratorType)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (decoratorType == null) throw new ArgumentNullException(nameof(decoratorType));
            _decorations.Add((serviceType, decoratorType));
        }

        public bool IsRegistered(Type serviceType, bool includeInterfaceTypes = true)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (_registeredServiceTypes.Contains(serviceType)) return true;
            if (includeInterfaceTypes)
            {
                if (_implementationTypes.ContainsKey(serviceType)) return true;
            }

            if (_initialized && _container != null && _container.TryGetRegistration(serviceType, out var registration))
            {
                if (includeInterfaceTypes || registration.ImplementationType == serviceType)
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

        public TService Resolve<TService>()
        {
            return (TService)Resolve(typeof(TService));
        }

        public object Resolve(Type serviceType)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (!_initialized || _container == null) throw new InvalidOperationException("Context not initialized");

            // VContainer can instantiate Unity-bound objects during resolve. Ensure resolve happens
            // on Unity main thread when runtime flow continues from a worker thread.
            return DispatchToMainThread(() => ResolveCore(serviceType), $"resolve '{serviceType.FullName}'");
        }

        private object ResolveCore(Type serviceType)
        {
            if (_decoratedInstances.TryGetValue(serviceType, out var decorated))
                return decorated;

            // Try own container first; fall back to parent only if this context doesn't have the registration
            if (_parent != null && !IsRegistered(serviceType))
            {
                return _parent.Resolve(serviceType);
            }

            return _container.Resolve(serviceType);
        }

        public void Initialize()
        {
            if (_initialized) return;

            // VContainer scope creation invokes build callbacks that may use Unity APIs
            // (Addressables, LayerMask, etc.) which require the main thread.
            // RuntimeFlow uses ConfigureAwait(false) so this method can be called
            // from a thread pool thread. Dispatch to main thread if needed.
            DispatchToMainThread(
                () =>
                {
                    InitializeCore();
                    return true;
                },
                "initialize context");
        }

        private void InitializeCore()
        {
            OnBeforeInitialize?.Invoke();

            // Validate decorations before building the container
            foreach (var (serviceType, _) in _decorations)
            {
                if (!IsRegistered(serviceType))
                    throw new InvalidOperationException(
                        $"Cannot decorate service '{serviceType.FullName}' because it is not registered.");
            }

            IObjectResolver? parentResolver = null;

            if (_parent is GameContext parentContext && parentContext._initialized)
            {
                parentResolver = parentContext._container;

                // If parent has a VContainer resolver registered, use it as external scope parent
                if (parentContext.TryGetRegisteredInstance(typeof(IObjectResolver), out var externalResolver))
                {
                    parentResolver = (IObjectResolver)externalResolver;
                }
            }

            if (parentResolver != null)
            {
                _container = parentResolver.CreateScope(ApplyRegistrations);
            }
            else
            {
                var builder = new VContainer.ContainerBuilder();
                ApplyRegistrations(builder);
                _container = builder.Build();
            }

            ApplyDecorations();

            _initialized = true;
            OnInitialized?.Invoke();
        }

        public void Dispose()
        {
            if (!_initialized) return;
            OnBeforeDispose?.Invoke();
            _decoratedInstances.Clear();

            DisposeOwnedRegisteredInstances();
            if (_container is IDisposable disposable)
                disposable.Dispose();

            foreach (var instanceProvider in _instanceProviders)
                instanceProvider.Release();

            _instanceProviders.Clear();
            _container = null;
            _initialized = false;

            _registrations.Clear();
            _registeredServiceTypes.Clear();
            _implementationTypes.Clear();
            _registeredInstances.Clear();
            _typedRegistrations.Clear();
            _instanceRegistrations.Clear();
            _decorations.Clear();

            var onDisposed = OnDisposed;
            OnBeforeInitialize = null;
            OnInitialized = null;
            OnBeforeDispose = null;
            OnDisposed = null;
            onDisposed?.Invoke();
        }

        internal bool TryGetImplementationType(Type serviceType, out Type implementationType)
        {
            if (_implementationTypes.TryGetValue(serviceType, out implementationType!))
                return true;

            if (_initialized
                && _container != null
                && _container.TryGetRegistration(serviceType, out var registration)
                && registration != null)
            {
                implementationType = registration.ImplementationType;
                _implementationTypes[serviceType] = implementationType;
                return true;
            }

            implementationType = null!;
            return false;
        }

        internal bool TryGetRegisteredInstance(Type serviceType, out object instance)
        {
            return _registeredInstances.TryGetValue(serviceType, out instance!);
        }

        internal KeyValuePair<Type, object>[] GetRegisteredInstanceEntriesSnapshot()
        {
            return _registeredInstances.ToArray();
        }

        internal void RegisterInstanceEx(
            Type implementationType,
            object instance,
            IReadOnlyCollection<Type> serviceTypes,
            bool ownsLifetime)
        {
            if (implementationType == null) throw new ArgumentNullException(nameof(implementationType));
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (serviceTypes == null) throw new ArgumentNullException(nameof(serviceTypes));

            var exposedTypes = serviceTypes
                .Where(type => type != null)
                .Distinct()
                .ToArray();

            if (exposedTypes.Length == 0)
                exposedTypes = new[] { implementationType };

            foreach (var serviceType in exposedTypes)
            {
                _registeredServiceTypes.Add(serviceType);
                _implementationTypes[serviceType] = implementationType;
                _registeredInstances[serviceType] = instance;
            }

            if (_instanceRegistrations.TryGetValue(implementationType, out var existing))
            {
                existing.instance = instance;
                foreach (var t in exposedTypes)
                {
                    if (!existing.interfaces.Contains(t))
                        existing.interfaces.Add(t);
                }

                existing.ownsLifetime &= ownsLifetime;
            }
            else
            {
                _instanceRegistrations[implementationType] =
                    (instance, new List<Type>(exposedTypes), ownsLifetime);
            }
        }

        private void AddTypedRegistration(Type implType, Lifetime lifetime, Type serviceType)
        {
            if (_typedRegistrations.TryGetValue(implType, out var existing))
            {
                if (!existing.interfaces.Contains(serviceType))
                    existing.interfaces.Add(serviceType);
            }
            else
            {
                _typedRegistrations[implType] = (lifetime, new List<Type> { serviceType });
            }
        }

        private void ApplyRegistrations(IContainerBuilder builder)
        {
            // Apply consolidated typed registrations first
            foreach (var (implType, (lifetime, interfaces)) in _typedRegistrations)
            {
                var rb = builder.Register(implType, lifetime);
                foreach (var iface in interfaces)
                    rb.As(iface);
            }

            // Apply consolidated instance registrations
            foreach (var (_, (instance, interfaces, _)) in _instanceRegistrations)
            {
                var provider = new RuntimeFlowInstanceProvider(instance);
                _instanceProviders.Add(provider);
                var rb = new RuntimeFlowInstanceRegistrationBuilder(instance.GetType(), provider);
                foreach (var t in interfaces)
                    rb.As(t);
                builder.Register(rb);
            }

            // Apply custom container configurations
            foreach (var registration in _registrations)
                registration(builder);
        }

        private void ApplyDecorations()
        {
            if (_decorations.Count == 0 || _container == null) return;

            foreach (var (serviceType, decoratorType) in _decorations)
            {
                var inner = _decoratedInstances.TryGetValue(serviceType, out var prev)
                    ? prev
                    : _container.Resolve(serviceType);

                var ctor = decoratorType.GetConstructors()
                    .OrderByDescending(c => c.GetParameters().Length).First();
                var parameters = ctor.GetParameters();
                var args = new object[parameters.Length];
                for (var i = 0; i < parameters.Length; i++)
                {
                    if (serviceType.IsAssignableFrom(parameters[i].ParameterType))
                        args[i] = inner;
                    else
                        args[i] = _container.Resolve(parameters[i].ParameterType);
                }

                _decoratedInstances[serviceType] = ctor.Invoke(args)!;
            }
        }

        private void DisposeOwnedRegisteredInstances()
        {
            var disposedInstances = new List<object>();

            foreach (var (instance, _, ownsLifetime) in _instanceRegistrations.Values.Reverse())
            {
                if (!ownsLifetime || instance is not IDisposable disposable)
                    continue;

                if (disposedInstances.Any(existing => ReferenceEquals(existing, instance)))
                    continue;

                disposable.Dispose();
                disposedInstances.Add(instance);
            }
        }

        internal static bool IsOnMainThread()
        {
            if (_mainThreadContext != null && SynchronizationContext.Current == _mainThreadContext)
                return true;

            return Thread.CurrentThread.ManagedThreadId == _mainThreadId;
        }

        private static T DispatchToMainThread<T>(Func<T> action, string operationDescription)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            if (_mainThreadContext == null || IsOnMainThread())
                return action();

            T? result = default;
            ExceptionDispatchInfo? capturedException = null;
            using var completed = new ManualResetEventSlim(false);
            _mainThreadContext.Post(_ =>
            {
                try
                {
                    result = action();
                }
                catch (Exception ex)
                {
                    capturedException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    completed.Set();
                }
            }, null);

            if (!completed.Wait(MainThreadDispatchTimeout))
            {
                throw new TimeoutException(
                    $"Timed out while waiting for main-thread dispatch to {operationDescription}.");
            }

            capturedException?.Throw();
            return result!;
        }

        /// <summary>
        /// Custom RegistrationBuilder subclass for pre-existing instances.
        /// hadashiA VContainer's internal InstanceRegistrationBuilder is not accessible,
        /// so we provide our own that returns a Registration with a fixed instance provider.
        /// </summary>
        private sealed class RuntimeFlowInstanceRegistrationBuilder : RegistrationBuilder
        {
            private readonly RuntimeFlowInstanceProvider _provider;

            public RuntimeFlowInstanceRegistrationBuilder(Type implementationType, RuntimeFlowInstanceProvider provider)
                : base(implementationType, Lifetime.Singleton)
            {
                _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            }

            public override Registration Build()
            {
                var types = InterfaceTypes.Count > 0
                    ? (IReadOnlyList<Type>)InterfaceTypes.ToList()
                    : new List<Type> { ImplementationType };
                return new Registration(
                    ImplementationType,
                    Lifetime,
                    types,
                    _provider);
            }
        }

        private sealed class RuntimeFlowInstanceProvider : IInstanceProvider
        {
            private object? _instance;
            private readonly string _implementationTypeName;

            public RuntimeFlowInstanceProvider(object instance)
            {
                _instance = instance ?? throw new ArgumentNullException(nameof(instance));
                var implementationType = instance.GetType();
                _implementationTypeName = implementationType.FullName ?? implementationType.Name;
            }

            public object SpawnInstance(IObjectResolver resolver)
            {
                return _instance
                    ?? throw new ObjectDisposedException(
                        _implementationTypeName,
                        $"Instance registration for '{_implementationTypeName}' was released when its RuntimeFlow scope was disposed.");
            }

            public void Release()
            {
                _instance = null;
            }
        }
    }
}
