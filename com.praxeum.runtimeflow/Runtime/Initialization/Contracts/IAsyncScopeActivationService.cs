using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    public interface IAsyncScopeActivationService
    {
        Task OnScopeActivatedAsync(CancellationToken cancellationToken);
        Task OnScopeDeactivatingAsync(CancellationToken cancellationToken);
    }

    public interface ISessionScopeActivationService : IAsyncScopeActivationService { }
    public interface ISceneScopeActivationService : IAsyncScopeActivationService { }
    public interface IModuleScopeActivationService : IAsyncScopeActivationService { }
}
