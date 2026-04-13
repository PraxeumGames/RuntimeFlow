using VContainer;

namespace VContainer.Unity
{
    public interface IInitializable
    {
        void Initialize();
    }

    public interface IStartable
    {
        void Start();
    }

    public interface IScopedObjectResolver : IObjectResolver
    {
        IObjectResolver? Parent { get; }
    }

    public sealed class LifetimeScope
    {
        public IObjectResolver? Container { get; set; }
    }
}
