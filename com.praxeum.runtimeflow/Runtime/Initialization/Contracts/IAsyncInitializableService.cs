using System;
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
}
