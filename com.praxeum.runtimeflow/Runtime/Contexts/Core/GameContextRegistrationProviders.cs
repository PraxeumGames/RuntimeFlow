using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VContainer;

namespace RuntimeFlow.Contexts
{
    internal sealed class RuntimeFlowInstanceRegistrationBuilder : RegistrationBuilder
    {
        private readonly RuntimeFlowInstanceProvider _provider;

        public RuntimeFlowInstanceRegistrationBuilder(Type implementationType, RuntimeFlowInstanceProvider provider)
            : base(implementationType, Lifetime.Singleton)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        public override Registration Build()
        {
            var types = InterfaceTypes.Count > 0
                ? (IReadOnlyList<Type>)InterfaceTypes.ToList()
                : new List<Type> { ImplementationType };

            return new Registration(
                ImplementationType,
                Lifetime,
                types,
                _provider);
        }
    }

    internal sealed class RuntimeFlowInstanceProvider : IInstanceProvider
    {
        private object? _instance;
        private readonly object _originalInstance;
        private int _spawned;
        private readonly string _implementationTypeName;

        public RuntimeFlowInstanceProvider(object instance)
        {
            _instance = instance ?? throw new ArgumentNullException(nameof(instance));
            _originalInstance = instance;
            var implementationType = instance.GetType();
            _implementationTypeName = implementationType.FullName ?? implementationType.Name;
        }

        /// <summary>
        /// True once the instance has been resolved through the VContainer container.
        /// Resolved instances are tracked and disposed by the container itself, so the
        /// RuntimeFlow manual disposal path must skip them to avoid double-disposal.
        /// </summary>
        public bool WasSpawned => _spawned != 0;

        public object Instance => _originalInstance;

        public object SpawnInstance(IObjectResolver resolver)
        {
            Interlocked.Exchange(ref _spawned, 1);
            return _instance
                ?? throw new ObjectDisposedException(
                    _implementationTypeName,
                    $"Instance registration for '{_implementationTypeName}' was released when its RuntimeFlow scope was disposed.");
        }

        public void Release()
        {
            _instance = null;
        }
    }
}
