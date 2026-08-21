using System;
using System.Collections.Generic;
using System.Threading;
using VContainer;
using VContainer.Diagnostics;
using VContainer.Internal;

namespace RuntimeFlow.Contexts
{
    public sealed class GameContext : IGameContext, IObjectResolver, IAsyncDisposable
    {
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

        internal static bool IsOnMainThread() => GameContextThreadDispatcher.IsOnMainThread();

        private readonly GameContextCore _core;

        internal GameContextCore Core => _core;

        public event Action? OnBeforeInitialize
        {
            add => _core.OnBeforeInitialize += value;
            remove => _core.OnBeforeInitialize -= value;
        }

        public event Action? OnInitialized
        {
            add => _core.OnInitialized += value;
            remove => _core.OnInitialized -= value;
        }

        public event Action? OnBeforeDispose
        {
            add => _core.OnBeforeDispose += value;
            remove => _core.OnBeforeDispose -= value;
        }

        public event Action? OnDisposed
        {
            add => _core.OnDisposed += value;
            remove => _core.OnDisposed -= value;
        }

        internal bool IsInitialized => _core.IsInitialized;
        internal bool IsDisposed => _core.IsDisposed;
        internal IReadOnlyList<ServiceInitializerBinding> InitializationOrder => _core.InitializationOrder;
        internal IReadOnlyCollection<Type> RegisteredServiceTypes => _core.RegisteredServiceTypes;

        internal IInitializationExecutionScheduler? ExecutionScheduler
        {
            get => _core.ExecutionScheduler;
            set => _core.ExecutionScheduler = value;
        }

        public IObjectResolver Resolver => _core.Resolver;
        public IGameContext? Parent => _core.Parent;
        public DiagnosticsCollector Diagnostics { get => _core.Diagnostics; set => _core.Diagnostics = value; }
        public object ApplicationOrigin => _core.ApplicationOrigin;

        public GameContext(IGameContext? parent = null)
        {
            _core = new GameContextCore(parent);
            _core.SetOwnerResolver(this);
        }

        internal void RecordInitialized(ServiceInitializerBinding initializer) => _core.RecordInitialized(initializer);

        public void Register<TService, TImplementation>() where TImplementation : TService => _core.Register<TService, TImplementation>();
        public void Register(Type serviceType, Type implementationType) => _core.Register(serviceType, implementationType);
        public void Register(Type serviceType, Type implementationType, Lifetime lifetime) => _core.Register(serviceType, implementationType, lifetime);
        public void ConfigureContainer(Action<IContainerBuilder> configure) => _core.ConfigureContainer(configure);
        public void Decorate(Type serviceType, Type decoratorType) => _core.Decorate(serviceType, decoratorType);
        public bool IsRegistered(Type serviceType, bool includeInterfaceTypes = true) => _core.IsRegistered(serviceType, includeInterfaceTypes);
        public void RegisterInstance(Type serviceType, object instance) => _core.RegisterInstance(serviceType, instance);
        public void RegisterInstance<TService>(TService instance) => _core.RegisterInstance(instance);
        public void RegisterInstance(object instance, IReadOnlyCollection<Type> serviceTypes) => _core.RegisterInstance(instance, serviceTypes);
        internal void RegisterImportedInstance(object instance, IReadOnlyCollection<Type> serviceTypes) => _core.RegisterImportedInstance(instance, serviceTypes);
        public TService Resolve<TService>() => _core.Resolve<TService>();
        public object Resolve(Type serviceType) => _core.Resolve(serviceType);
        public System.Threading.Tasks.Task<TService> ResolveAsync<TService>(System.Threading.CancellationToken cancellationToken = default) => _core.ResolveAsync<TService>(cancellationToken);
        public System.Threading.Tasks.Task<object> ResolveAsync(Type serviceType, System.Threading.CancellationToken cancellationToken = default) => _core.ResolveAsync(serviceType, cancellationToken);
        internal object Resolve(ServiceInitializerBinding initializer) => _core.Resolve(initializer);
        internal bool TryGetImplementationType(Type serviceType, out Type implementationType) => _core.TryGetImplementationType(serviceType, out implementationType);
        internal IReadOnlyList<Registration> GetRegistrationsForServiceType(Type serviceType) => _core.GetRegistrationsForServiceType(serviceType);
        internal bool TryGetRegisteredInstance(Type serviceType, out object instance) => _core.TryGetRegisteredInstance(serviceType, out instance);
        internal void RegisterInstanceEx(Type implementationType, object instance, IReadOnlyCollection<Type> serviceTypes, bool ownsLifetime) => _core.RegisterInstanceEx(implementationType, instance, serviceTypes, ownsLifetime);
        public void Initialize() => _core.Initialize();
        public object Resolve(Registration registration) => _core.Resolve(registration);
        public bool TryResolve(Type serviceType, out object resolved) => _core.TryResolve(serviceType, out resolved);
        public void Inject(object instance) => _core.Inject(instance);
        public bool TryGetRegistration(Type type, out Registration registration) => _core.TryGetRegistration(type, out registration);
        IScopedObjectResolver IObjectResolver.CreateScope(Action<IContainerBuilder> installation) => ((IObjectResolver)_core).CreateScope(installation);
        public void Dispose() => _core.Dispose();
        public System.Threading.Tasks.ValueTask DisposeAsync(System.Threading.CancellationToken cancellationToken = default) => _core.DisposeAsync(cancellationToken);
        System.Threading.Tasks.ValueTask IAsyncDisposable.DisposeAsync() => _core.DisposeAsync();
    }
}
