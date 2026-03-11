using System;

namespace RuntimeFlow.Contexts
{
    /// <summary>Base exception for all RuntimeFlow framework errors.</summary>
    public class RuntimeFlowException : Exception
    {
        public RuntimeFlowException(string message) : base(message) { }
        public RuntimeFlowException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>Thrown when a scope type is used but was never declared via Define*Scope.</summary>
    public class ScopeNotDeclaredException : RuntimeFlowException
    {
        public Type ScopeType { get; }
        public ScopeNotDeclaredException(Type scopeType)
            : base($"Scope type '{scopeType.FullName ?? scopeType.Name}' is not declared. Declare it via Define*Scope<TScope>() before use.")
        {
            ScopeType = scopeType;
        }
    }

    /// <summary>Thrown when attempting to restart/reload a non-restartable scope (Global).</summary>
    public class ScopeNotRestartableException : RuntimeFlowException
    {
        public Type ScopeType { get; }
        public ScopeNotRestartableException(Type scopeType)
            : base($"Scope '{scopeType.Name}' is non-restartable by design.")
        {
            ScopeType = scopeType;
        }
    }

    /// <summary>Thrown when trying to use a scope that hasn't been initialized yet.</summary>
    public class ScopeNotInitializedException : RuntimeFlowException
    {
        public Type ScopeType { get; }
        public ScopeNotInitializedException(Type scopeType)
            : base($"Scope '{scopeType.Name}' has not been initialized. Call InitializeAsync or BuildAsync first.")
        {
            ScopeType = scopeType;
        }
    }

    /// <summary>Thrown when RunAsync is called without ConfigureFlow.</summary>
    public class FlowNotConfiguredException : RuntimeFlowException
    {
        public FlowNotConfiguredException()
            : base("Flow is not configured. Call ConfigureFlow(...) before RunAsync().") { }
    }

    /// <summary>Thrown for scope registration validation errors (GBSR diagnostics).</summary>
    public class ScopeRegistrationException : RuntimeFlowException
    {
        public string DiagnosticCode { get; }
        public ScopeRegistrationException(string diagnosticCode, string message)
            : base(message)
        {
            DiagnosticCode = diagnosticCode;
        }
    }
}
