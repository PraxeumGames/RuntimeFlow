using System;
using System.Threading;
using System.Threading.Tasks;
using VContainer;

namespace RuntimeFlow.Contexts
{
    /// <summary>Fluent builder for registering services within a specific scope.</summary>
    public interface IGameScopeRegistrationBuilder
    {
        IGameScopeRegistrationBuilder Register<TInterface, TImplementation>(Lifetime lifetime) where TImplementation : class, TInterface;
        IGameScopeRegistrationBuilder Register<TImplementation>(Lifetime lifetime) where TImplementation : class;
        IGameScopeRegistrationBuilder Register(Type implementationType, Lifetime lifetime);
        IGameScopeRegistrationBuilder As<TInterface>();
        IGameScopeRegistrationBuilder As(Type interfaceType);
        IGameScopeRegistrationBuilder AsSelf();
        IGameScopeRegistrationBuilder RegisterInstance<TInterface>(TInterface instance);
        IGameScopeRegistrationBuilder Import<TImplementation>(IObjectResolver resolver, params Type[] additionalServiceTypes);
        IGameScopeRegistrationBuilder Decorate<TService, TDecorator>() where TDecorator : class, TService;
        IGameScopeRegistrationBuilder ConfigureContainer(Action<VContainer.IContainerBuilder> configure);
    }

    /// <summary>Defines and builds the hierarchical scope graph (Global → Session → Scene → Module).</summary>
    public interface IGameContextBuilder
    {
        /// <summary>Returns a registration builder for the Global scope.</summary>
        IGameScopeRegistrationBuilder Global();

        /// <summary>Returns a registration builder for the Session scope.</summary>
        IGameScopeRegistrationBuilder Session();

        /// <summary>Declares the built-in Global scope.</summary>
        IGameContextBuilder DefineGlobalScope();

        /// <summary>Declares the built-in Session scope.</summary>
        IGameContextBuilder DefineSessionScope();

        /// <summary>
        /// Defines a scene scope using an installer class.
        /// The installer's <see cref="IScopeInstaller.Configure"/> method is called to register services.
        /// </summary>
        IGameContextBuilder Scene<TScope>() where TScope : ISceneScope, new();

        /// <summary>
        /// Defines a scene scope using an existing installer instance.
        /// The installer's <see cref="IScopeInstaller.Configure"/> method is called to register services.
        /// </summary>
        IGameContextBuilder Scene<TScope>(TScope installer) where TScope : ISceneScope;

        /// <summary>
        /// Defines a module scope using an installer class.
        /// The installer's <see cref="IScopeInstaller.Configure"/> method is called to register services.
        /// </summary>
        IGameContextBuilder Module<TScope>() where TScope : IModuleScope, new();

        /// <summary>
        /// Defines a module scope using an existing installer instance.
        /// The installer's <see cref="IScopeInstaller.Configure"/> method is called to register services.
        /// </summary>
        IGameContextBuilder Module<TScope>(TScope installer) where TScope : IModuleScope;

        bool TryResolveScopeType(Type scopeType, out GameContextType scope);
        ScopeLifecycleState GetScopeLifecycleState(Type scopeType);
        IGameContextBuilder OnGlobalInitialized(Action<IGameContext> callback);
        IGameContextBuilder OnSessionInitialized(Action<IGameContext> callback);
        IGameContextBuilder OnSceneInitialized(Action<IGameContext> callback);
        IGameContextBuilder OnModuleInitialized(Action<IGameContext> callback);
        Task<IGameContext> BuildAsync(IInitializationProgressNotifier? progressNotifier = null, CancellationToken cancellationToken = default);
        Task RestartSessionAsync(IInitializationProgressNotifier? progressNotifier = null, CancellationToken cancellationToken = default);
        Task LoadAdditiveModuleAsync(Type moduleScopeKey, IInitializationProgressNotifier? progressNotifier = null, CancellationToken cancellationToken = default);
        Task UnloadAdditiveModuleAsync(Type moduleScopeKey, CancellationToken cancellationToken = default);
    }

    public static class GameContextBuilderExtensions
    {
        /// <summary>Defines a scene scope and chains additional module scopes fluently.</summary>
        public static IGameContextBuilder WithScene<TScope>(this IGameContextBuilder builder) where TScope : ISceneScope, new()
        {
            return builder.Scene<TScope>();
        }

        /// <summary>Defines a module scope fluently.</summary>
        public static IGameContextBuilder WithModule<TScope>(this IGameContextBuilder builder) where TScope : IModuleScope, new()
        {
            return builder.Module<TScope>();
        }

        /// <summary>Defines a module scope with an existing installer instance fluently.</summary>
        public static IGameContextBuilder WithModule<TScope>(this IGameContextBuilder builder, TScope installer) where TScope : IModuleScope
        {
            return builder.Module(installer);
        }

        /// <summary>Registers a transition handler in the Session scope.</summary>
        public static IGameContextBuilder WithTransition<TTransition>(this IGameContextBuilder builder) where TTransition : class, IScopeTransitionHandler
        {
            builder.Session().Register<IScopeTransitionHandler, TTransition>(Lifetime.Singleton);
            return builder;
        }
    }
}
