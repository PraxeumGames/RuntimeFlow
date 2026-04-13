using System;
using System.Collections.Generic;
using System.Threading;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public class GameContext : IGameContext
    {
        /// <summary>
        /// The main-thread SynchronizationContext captured at startup.
        /// Use for marshaling Unity API calls from background threads.
        /// </summary>
        public static SynchronizationContext? MainThreadContext => GameContextThreadDispatcher.MainThreadContext;

#if UNITY_5_3_OR_NEWER
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
#endif
        private static void CaptureMainThread()
        {
            GameContextThreadDispatcher.CaptureMainThread();
        }

#if UNITY_5_3_OR_NEWER
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
#endif
        private static void CaptureMainThreadContext()
        {
            GameContextThreadDispatcher.CaptureMainThreadContext();
        }

        private readonly IGameContext? _parent;
        private readonly GameContextRegistrationStore _registrationStore = new();
        private readonly GameContextDecorationChain _decorationChain = new();
        private readonly List<RuntimeFlowInstanceProvider> _instanceProviders;
        private IObjectResolver? _container;
        private bool _initialized;

        public event Action? OnBeforeInitialize;
        public event Action? OnInitialized;
        public event Action? OnBeforeDispose;
        public event Action? OnDisposed;

        public IObjectResolver Resolver => _container ?? throw new InvalidOperationException("Context not initialized");
        public IGameContext? Parent => _parent;
        internal IReadOnlyCollection<Type> RegisteredServiceTypes => _registrationStore.RegisteredServiceTypes;

        public GameContext(IGameContext? parent = null)
        {
            _parent = parent;
            _instanceProviders = _registrationStore.InstanceProviders;
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
            _registrationStore.Register(serviceType, implType, Lifetime.Singleton);
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

            _registrationStore.Register(serviceType, implementationType, lifetime);
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
            return _registrationStore.IsRegistered(serviceType, includeInterfaceTypes, _initialized, _container);
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
            if (_decorationChain.TryGetDecoratedInstance(serviceType, out var decorated))
                return decorated;

            // Try own container first; fall back to parent only if this context doesn't have the registration
            if (_parent != null && !IsRegistered(serviceType))
            {
                return _parent.Resolve(serviceType);
            }

            return _container!.Resolve(serviceType);
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

            _decorationChain.ValidateRegistrations(serviceType => IsRegistered(serviceType));

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
                _container = parentResolver.CreateScope(_registrationStore.ApplyRegistrations);
            }
            else
            {
                var builder = new VContainer.ContainerBuilder();
                _registrationStore.ApplyRegistrations(builder);
                _container = builder.Build();
            }

            _decorationChain.Apply(_container);

            _initialized = true;
            OnInitialized?.Invoke();
        }

        public void Dispose()
        {
            if (!_initialized) return;
            List<Exception>? disposeFailures = null;
            var onDisposed = OnDisposed;

            try
            {
                OnBeforeDispose?.Invoke();
            }
            catch (Exception ex)
            {
                AddDisposeFailure(ref disposeFailures, ex);
            }

            _decorationChain.ClearResolvedInstances();

            DisposeOwnedRegisteredInstances(ref disposeFailures);
            if (_container is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    AddDisposeFailure(ref disposeFailures, ex);
                }
            }

            foreach (var instanceProvider in _instanceProviders)
            {
                try
                {
                    instanceProvider.Release();
                }
                catch (Exception ex)
                {
                    AddDisposeFailure(ref disposeFailures, ex);
                }
            }

            _registrationStore.ClearProviderInstances();
            _container = null;
            _initialized = false;

            _registrationStore.ClearRegistrations();
            _decorationChain.Clear();

            OnBeforeInitialize = null;
            OnInitialized = null;
            OnBeforeDispose = null;
            OnDisposed = null;
            try
            {
                onDisposed?.Invoke();
            }
            catch (Exception ex)
            {
                AddDisposeFailure(ref disposeFailures, ex);
            }

            if (disposeFailures is { Count: > 0 })
            {
                throw new AggregateException(
                    "GameContext disposal encountered one or more failures.",
                    disposeFailures);
            }
        }

        internal bool TryGetImplementationType(Type serviceType, out Type implementationType)
        {
            return _registrationStore.TryGetImplementationType(serviceType, _initialized, _container, out implementationType);
        }

        internal bool TryGetRegisteredInstance(Type serviceType, out object instance)
        {
            return _registrationStore.TryGetRegisteredInstance(serviceType, out instance!);
        }

        internal KeyValuePair<Type, object>[] GetRegisteredInstanceEntriesSnapshot()
        {
            return _registrationStore.GetRegisteredInstanceEntriesSnapshot();
        }

        internal void RegisterInstanceEx(
            Type implementationType,
            object instance,
            IReadOnlyCollection<Type> serviceTypes,
            bool ownsLifetime)
        {
            _registrationStore.RegisterInstance(implementationType, instance, serviceTypes, ownsLifetime);
        }

        private void DisposeOwnedRegisteredInstances(ref List<Exception>? disposeFailures)
        {
            _registrationStore.DisposeOwnedRegisteredInstances(ref disposeFailures);
        }

        private static void AddDisposeFailure(ref List<Exception>? failures, Exception exception)
        {
            failures ??= new List<Exception>();
            failures.Add(exception);
        }

        internal static bool IsOnMainThread()
        {
            return GameContextThreadDispatcher.IsOnMainThread();
        }

        private static T DispatchToMainThread<T>(Func<T> action, string operationDescription)
        {
            return GameContextThreadDispatcher.DispatchToMainThread(action, operationDescription);
        }
    }
}
