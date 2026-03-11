using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using VContainer;
using VContainer.Internal;
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
                var rb = new RuntimeFlowInstanceRegistration(instance);
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
        /// Custom VContainer IRegistrationBuilder for pre-existing instances.
        /// VContainer's InstanceRegistrationBuilder is internal, so we need our own.
        /// </summary>
        private sealed class RuntimeFlowInstanceRegistration : IRegistrationBuilder
        {
            private readonly object _instance;

            public Type ImplementationType { get; }
            public Lifetime Lifetime => Lifetime.Singleton;
            public List<Type> InterfaceTypes { get; } = new();
            public List<IInjectParameter> Parameters { get; } = new();
            public BindingCondition Condition => default;

            public RuntimeFlowInstanceRegistration(object instance)
            {
                _instance = instance;
                ImplementationType = instance.GetType();
            }

            public IRegistration Build()
            {
                return new RuntimeFlowInstanceRegistrationResult(
                    ImplementationType, InterfaceTypes, new RuntimeFlowInstanceProvider(_instance));
            }

            public IRegistrationBuilder As<TInterface>() => As(typeof(TInterface));
            public IRegistrationBuilder As<T1, T2>() { As(typeof(T1)); return As(typeof(T2)); }
            public IRegistrationBuilder As<T1, T2, T3>() { As(typeof(T1)); As(typeof(T2)); return As(typeof(T3)); }
            public IRegistrationBuilder As<T1, T2, T3, T4>() { As(typeof(T1)); As(typeof(T2)); As(typeof(T3)); return As(typeof(T4)); }
            public IRegistrationBuilder AsSelf() => As(ImplementationType);
            public IRegistrationBuilder AsImplementedInterfaces()
            {
                InterfaceTypes.AddRange(ImplementationType.GetInterfaces());
                return this;
            }

            public IRegistrationBuilder As(Type interfaceType)
            {
                if (!InterfaceTypes.Contains(interfaceType))
                    InterfaceTypes.Add(interfaceType);
                return this;
            }

            public IRegistrationBuilder As(Type t1, Type t2) { As(t1); return As(t2); }
            public IRegistrationBuilder As(Type t1, Type t2, Type t3) { As(t1); As(t2); return As(t3); }
            public IRegistrationBuilder As(params Type[] interfaceTypes)
            {
                foreach (var t in interfaceTypes) As(t);
                return this;
            }
            public IRegistrationBuilder WithParameter(Type type, object value) => this;
            public IRegistrationBuilder WithParameter(string name, object value) => this;
            public IRegistrationBuilder WithParameter<TParam>(TParam value) => this;
            public IRegistrationBuilder WhenInjectedInto<T>() => this;
            public IRegistrationBuilder WhenNotInjectedInto<T>() => this;
            public void AddInterfaceType(Type interfaceType) => As(interfaceType);
        }

        private sealed class RuntimeFlowInstanceProvider : IInstanceProvider
        {
            private readonly object _instance;
            public RuntimeFlowInstanceProvider(object instance) => _instance = instance;
            public object SpawnInstance(IObjectResolver resolver) => _instance;
        }

        private sealed class RuntimeFlowInstanceRegistrationResult : IRegistration
        {
            public Type ImplementationType { get; }
            public List<Type> InterfaceTypes { get; }
            public Lifetime Lifetime => Lifetime.Singleton;
            public IInstanceProvider Provider { get; }
            public BindingCondition? Condition => null;

            public RuntimeFlowInstanceRegistrationResult(
                Type implementationType, List<Type> interfaceTypes, IInstanceProvider provider)
            {
                ImplementationType = implementationType;
                InterfaceTypes = interfaceTypes;
                Provider = provider;
            }

            public object SpawnInstance(IObjectResolver resolver) => Provider.SpawnInstance(resolver);
        }
    }
}
