using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    /// <summary>A service that requires asynchronous initialization after DI construction.</summary>
    public interface IAsyncInitializableService
    {
        Task InitializeAsync(CancellationToken cancellationToken);
    }

    /// <summary>Marker for services initialized during the global scope phase.</summary>
    public interface IGlobalInitializableService : IAsyncInitializableService { }
    /// <summary>Marker for services initialized during the session scope phase.</summary>
    public interface ISessionInitializableService : IAsyncInitializableService { }
    /// <summary>Marker for services initialized during the scene scope phase.</summary>
    public interface ISceneInitializableService : IAsyncInitializableService { }
    /// <summary>Marker for services initialized during the module scope phase.</summary>
    public interface IModuleInitializableService : IAsyncInitializableService { }

    /// <summary>
    /// Cross-cutting trait marker for a service whose <see cref="IAsyncInitializableService.InitializeAsync"/>
    /// legitimately blocks on user/player interaction (consent, migration, progress choice, account conflict,
    /// or any modal dialog) for an unbounded amount of time.
    ///
    /// Such a service is exempt from the health watchdog: <see cref="RuntimeHealthSupervisor.GetServiceTimeout"/>
    /// returns <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> for it, so the pipeline is not torn down
    /// while the player is looking at the dialog. This is orthogonal to the scope markers — a service combines
    /// this trait with its scope marker, e.g. <c>: ISessionInitializableService, IUserInteractionGatedInitializableService</c>.
    ///
    /// It is a marker-only type (see <see cref="InitializationContractCatalog"/>): RuntimeFlow never records a
    /// service under this interface, so any number of services may implement it without a discovery collision.
    /// </summary>
    public interface IUserInteractionGatedInitializableService : IAsyncInitializableService { }

    /// <summary>Marker for services initialized during session startup stages.</summary>
    public interface IStartupStageInitializableService : ISessionInitializableService { }

    /// <summary>Marker for services initialized during PreBootstrap startup stage.</summary>
    public interface IPreBootstrapStartupInitializableService : IStartupStageInitializableService { }
    /// <summary>Marker for services initialized during Platform startup stage.</summary>
    public interface IPlatformStartupInitializableService : IStartupStageInitializableService { }
    /// <summary>Marker for services initialized during Content startup stage.</summary>
    public interface IContentStartupInitializableService : IStartupStageInitializableService { }
    /// <summary>Marker for services initialized during Session startup stage.</summary>
    public interface ISessionStartupInitializableService : IStartupStageInitializableService { }
    /// <summary>Marker for services initialized during UI startup stage.</summary>
    public interface IUiStartupInitializableService : IStartupStageInitializableService { }

    /// <summary>Deterministic startup stages used by session startup orchestration.</summary>
    public enum RuntimeFlowStartupStage
    {
        PreBootstrap = 0,
        Platform = 1,
        Content = 2,
        Session = 3,
        UI = 4
    }

    /// <summary>Mapping between a startup stage and its marker service interface.</summary>
    public sealed class RuntimeFlowStartupStageContract
    {
        public RuntimeFlowStartupStageContract(RuntimeFlowStartupStage stage, Type serviceMarkerType)
        {
            if (serviceMarkerType == null) throw new ArgumentNullException(nameof(serviceMarkerType));
            Stage = stage;
            ServiceMarkerType = serviceMarkerType;
        }

        public RuntimeFlowStartupStage Stage { get; }

        public Type ServiceMarkerType { get; }
    }

    /// <summary>Canonical startup stage order for stage-aware session bootstrap.</summary>
    public static class RuntimeFlowStartupStageContracts
    {
        private static readonly RuntimeFlowStartupStageContract[] OrderedContracts =
        {
            new(RuntimeFlowStartupStage.PreBootstrap, typeof(IPreBootstrapStartupInitializableService)),
            new(RuntimeFlowStartupStage.Platform, typeof(IPlatformStartupInitializableService)),
            new(RuntimeFlowStartupStage.Content, typeof(IContentStartupInitializableService)),
            new(RuntimeFlowStartupStage.Session, typeof(ISessionStartupInitializableService)),
            new(RuntimeFlowStartupStage.UI, typeof(IUiStartupInitializableService))
        };

        public static IReadOnlyList<RuntimeFlowStartupStageContract> Ordered => OrderedContracts;
    }
}
