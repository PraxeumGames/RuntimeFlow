using System;
using System.Collections.Generic;
using System.Threading;
using VContainer;

namespace RuntimeFlow.Contexts
{
    public partial class GameContext : IGameContext
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
