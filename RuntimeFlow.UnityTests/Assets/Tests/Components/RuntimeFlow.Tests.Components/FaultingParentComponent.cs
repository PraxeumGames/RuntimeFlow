using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VContainer;

namespace RuntimeFlow.Tests.Components
{
    public interface IParentFaultingComponent { }
    public interface IChildFaultingComponent { }
    public interface IFaultingComponentAuth { bool FailInjection { get; } }

    public sealed class ParentComponentAuth : IFaultingComponentAuth
    {
        public bool FailInjection => false;
    }

    public sealed class ChildComponentAuth : IFaultingComponentAuth
    {
        public bool FailInjection => true;
    }

    public sealed class FaultingParentComponent : MonoBehaviour, IAsyncInitializable, IParentFaultingComponent, IChildFaultingComponent
    {
        public IFaultingComponentAuth Auth { get; private set; } = null!;
        public int Injections { get; private set; }
        public int Initializations { get; private set; }

        [Inject]
        public void Construct(IFaultingComponentAuth auth)
        {
            Auth = auth;
            Injections++;
            if (auth.FailInjection) throw new InvalidOperationException("injection failed after mutating the parent");
        }

        public Task InitializeAsync(InitContext context, CancellationToken token)
        {
            Initializations++;
            return Task.CompletedTask;
        }
    }
}
