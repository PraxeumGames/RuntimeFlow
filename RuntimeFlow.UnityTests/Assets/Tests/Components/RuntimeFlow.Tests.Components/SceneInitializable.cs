using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VContainer;

namespace RuntimeFlow.Tests.Components
{
    /// <summary>
    /// A scene component registered with <c>RegisterComponentInHierarchy</c> (Lifetime.Scoped): counts how
    /// often VContainer injects it and how often it is initialized.
    /// </summary>
    public sealed class SceneInitializable : MonoBehaviour, IAsyncInitializable
    {
        public int Injections { get; private set; }
        public int Initializations { get; private set; }

        [Inject]
        public void Construct(IObjectResolver resolver) => Injections++;

        public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
        {
            Initializations++;
            return Task.CompletedTask;
        }
    }
}
