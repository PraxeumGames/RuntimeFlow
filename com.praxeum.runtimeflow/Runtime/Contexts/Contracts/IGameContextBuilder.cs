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
        IGameScopeRegistrationBuilder For<TScope>();
        IGameScopeRegistrationBuilder Global();
        IGameScopeRegistrationBuilder Session();
        IGameContextBuilder DefineGlobalScope();
        IGameContextBuilder DefineGlobalScope<TScope>();
        IGameContextBuilder DefineSessionScope();
        IGameContextBuilder DefineSessionScope<TScope>();
        IGameContextBuilder DefineSceneScope<TScope>();
        IGameContextBuilder DefineModuleScope<TScope>();
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
}
