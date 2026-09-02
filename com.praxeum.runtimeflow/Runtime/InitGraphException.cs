using System;

namespace RuntimeFlow
{
    /// <summary>
    /// A graph problem detected before any service is initialized: a cycle, an unknown
    /// <see cref="DependsOnAttribute"/> target, a non-singleton registration, an unknown phase or a
    /// conflicting <see cref="InitAttribute"/>.
    /// </summary>
    public sealed class InitGraphException : Exception
    {
        /// <summary>Creates the exception for <paramref name="scope"/> with the fully formatted message.</summary>
        public InitGraphException(string scope, string message, Exception? innerException = null)
            : base(message, innerException)
            => Scope = scope;

        /// <summary>Name of the scope whose graph is invalid.</summary>
        public string Scope { get; }
    }
}
