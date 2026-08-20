using System;
using System.Collections.Generic;
using System.Threading;
using VContainer;
using VContainer.Internal;

namespace RuntimeFlow.Contexts
{
    public partial class GameContext : IGameContext, IObjectResolver
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
        private readonly Dictionary<Registration, object> _sharedInstances = new();
        private readonly List<object> _ownedRegisteredInstances = new();
        private readonly List<object> _ownedResolvedInstances = new();
        private Registry? _registry;
        private bool _initialized;

        public event Action? OnBeforeInitialize;
        public event Action? OnInitialized;
        public event Action? OnBeforeDispose;
        public event Action? OnDisposed;

        /// <summary>
        /// This context is its own resolver: GameContext is the container. There is no
        /// separate VContainer container behind this property.
        /// </summary>
        public IObjectResolver Resolver => _initialized
            ? this
            : throw new InvalidOperationException("Context not initialized");

        public IGameContext? Parent => _parent;
        internal IReadOnlyCollection<Type> RegisteredServiceTypes => _registrationStore.RegisteredServiceTypes;

        public GameContext(IGameContext? parent = null)
        {
            _parent = parent;
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