using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Reference-equality comparer for ownership tracking and teardown deduplication.
    /// </summary>
    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        private ReferenceEqualityComparer()
        {
        }

        public new bool Equals(object? x, object? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(object obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}