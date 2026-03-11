using System;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Declares an explicit initialization dependency on another service.
    /// RuntimeFlow will ensure the specified service is initialized before this one.
    /// Use this instead of adding unused constructor parameters for ordering.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class DependsOnAttribute : Attribute
    {
        public Type ServiceType { get; }

        public DependsOnAttribute(Type serviceType)
        {
            ServiceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));
        }
    }
}
