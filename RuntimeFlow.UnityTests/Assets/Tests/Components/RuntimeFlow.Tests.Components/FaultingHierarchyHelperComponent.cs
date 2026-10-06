using VContainer;

namespace RuntimeFlow.Tests.Components
{
    public sealed class FaultingHierarchyHelperComponent : UnsafeHierarchyBaseComponent
    {
        public IParentFaultingComponent Parent { get; private set; } = null!;

        [Inject]
        public void Construct(IParentFaultingComponent parent) => Parent = parent;
    }
}
