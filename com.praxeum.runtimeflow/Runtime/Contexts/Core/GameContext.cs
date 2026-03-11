using System;
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
            _mainThreadContext ??= SynchronizationContext.Current;
        }

        private readonly IGameContext? _parent;
        private readonly List<Action<VContainer.IContainerBuilder>> _registrations = new();
        private readonly HashSet<Type> _registeredServiceTypes = new();
        private readonly Dictionary<Type, Type> _implementationTypes = new();
        private readonly Dictionary<Type, object> _registeredInstances = new();
        private readonly Dictionary<Type, (Lifetime lifetime, List<Type> interfaces)> _typedRegistrations = new();
        private readonly Dictionary<Type, (object instance, List<Type> interfaces)> _instanceRegistrations = new();
        private readonly List<(Type serviceType, Type decoratorType)> _decorations = new();
        private readonly Dictionary<Type, object> _decoratedInstances = new();
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
            return false;
        }

        public void RegisterInstance(Type serviceType, object instance)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            RegisterInstanceEx(instance.GetType(), instance, new[] { serviceType });
        }

        public void RegisterInstance<TService>(TService instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            RegisterInstanceEx(instance.GetType(), instance, new[] { typeof(TService) });
        }

        public void RegisterInstance(object instance, IReadOnlyCollection<Type> serviceTypes)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (serviceTypes == null) throw new ArgumentNullException(nameof(serviceTypes));
            RegisterInstanceEx(instance.GetType(), instance, serviceTypes);
        }

        public TService Resolve<TService>()
        {
            return (TService)Resolve(typeof(TService));
        }

        public object Resolve(Type serviceType)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (!_initialized || _container == null) throw new InvalidOperationException("Context not initialized");

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
            if (_mainThreadContext != null && Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                ExceptionDispatchInfo? caught = null;
                _mainThreadContext.Send(_ =>
                {
                    try { InitializeCore(); }
                    catch (Exception ex) { caught = ExceptionDispatchInfo.Capture(ex); }
                }, null);
                caught?.Throw();
                return;
            }

            InitializeCore();
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
            if (_container is IDisposable disposable)
                disposable.Dispose();
            _container = null;
            _initialized = false;
            OnDisposed?.Invoke();
        }

        internal bool TryGetImplementationType(Type serviceType, out Type implementationType)
        {
            return _implementationTypes.TryGetValue(serviceType, out implementationType!);
        }

        internal bool TryGetRegisteredInstance(Type serviceType, out object instance)
        {
            return _registeredInstances.TryGetValue(serviceType, out instance!);
        }

        internal void RegisterInstanceEx(Type implementationType, object instance, IReadOnlyCollection<Type> serviceTypes)
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
            }
            else
            {
                _instanceRegistrations[implementationType] =
                    (instance, new List<Type>(exposedTypes));
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
            foreach (var (_, (instance, interfaces)) in _instanceRegistrations)
            {
                var rb = new RuntimeFlowInstanceRegistrationBuilder(instance);
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

        /// <summary>
        /// Custom RegistrationBuilder subclass for pre-existing instances.
        /// hadashiA VContainer's internal InstanceRegistrationBuilder is not accessible,
        /// so we provide our own that returns a Registration with a fixed instance provider.
        /// </summary>
        private sealed class RuntimeFlowInstanceRegistrationBuilder : RegistrationBuilder
        {
            private readonly object _instance;

            public RuntimeFlowInstanceRegistrationBuilder(object instance)
                : base(instance.GetType(), Lifetime.Singleton)
            {
                _instance = instance;
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
                    new RuntimeFlowInstanceProvider(_instance));
            }
        }

        private sealed class RuntimeFlowInstanceProvider : IInstanceProvider
        {
            private readonly object _instance;
            public RuntimeFlowInstanceProvider(object instance) => _instance = instance;
            public object SpawnInstance(IObjectResolver resolver) => _instance;
        }
    }
}
