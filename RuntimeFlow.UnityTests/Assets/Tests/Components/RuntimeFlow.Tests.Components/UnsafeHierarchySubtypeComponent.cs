using UnityEngine;
using VContainer;

namespace RuntimeFlow.Tests.Components
{
    public class UnsafeHierarchyBaseComponent : MonoBehaviour, IGraphSubtypeHelper { }

    public sealed class UnsafeHierarchySubtypeComponent : UnsafeHierarchyBaseComponent
    {
        [Inject]
        public SceneInitializable ParentInitializer { get; set; } = null!;
    }
}
