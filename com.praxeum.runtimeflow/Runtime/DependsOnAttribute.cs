using System;

namespace RuntimeFlow
{
    /// <summary>
    /// Adds an ordering edge to every initializable service assignable to <see cref="ServiceType"/>.
    /// Use it when the dependency is not visible as a constructor parameter; unknown targets are a graph error.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
    public sealed class DependsOnAttribute : Attribute
    {
        /// <summary>Creates an ordering edge towards services assignable to <paramref name="serviceType"/>.</summary>
        public DependsOnAttribute(Type serviceType) => ServiceType = serviceType;

        /// <summary>The declared dependency type; every node assignable to it becomes a dependency.</summary>
        public Type ServiceType { get; }
    }
}
