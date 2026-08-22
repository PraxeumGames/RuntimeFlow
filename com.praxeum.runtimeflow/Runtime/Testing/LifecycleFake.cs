using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Testing
{
    /// <summary>
    /// Builds a dispatch-proxy fake around a stub that participates in the RuntimeFlow
    /// lifecycle: initialization attempts can fail N times before succeeding, disposal can
    /// be observed or made to fail, and every intercepted call is recorded.
    ///
    /// Declare a test interface that combines your service contract with the lifecycle
    /// contracts you care about (the standard RuntimeFlow test idiom), then:
    /// <code>
    /// interface IFakePayments : IPaymentsService, ISessionInitializableService, IDisposable { }
    /// var fake = LifecycleFake.Of&lt;IFakePayments&gt;(new StubPayments(), cfg =&gt; cfg.FailInitializeAttempts(2));
    /// </code>
    /// </summary>
    public static class LifecycleFake
    {
        public static TService Of<TService>(TService? stub = null, Action<FakeBehavior>? configure = null)
            where TService : class
            => OfHandle(stub, configure).Service;

        /// <summary>Returns the fake together with its invocation log for assertions.</summary>
        public static LifecycleFakeHandle<TService> OfHandle<TService>(TService? stub = null, Action<FakeBehavior>? configure = null)
            where TService : class
        {
            if (!typeof(TService).IsInterface)
                throw new ArgumentException($"'{typeof(TService).Name}' must be an interface.", nameof(stub));

            var behavior = new FakeBehavior();
            configure?.Invoke(behavior);
            var proxy = (FakeDispatchProxy)(object)DispatchProxy.Create<TService, FakeDispatchProxy>()!;
            var bound = proxy.Bind(stub, behavior);
            return new LifecycleFakeHandle<TService>((TService)(object)bound, proxy.Log);
        }
    }

    public sealed class LifecycleFakeHandle<TService> where TService : class
    {
        public TService Service { get; }
        public FakeInvocationLog Log { get; }

        internal LifecycleFakeHandle(TService service, FakeInvocationLog log)
        {
            Service = service;
            Log = log;
        }

        public static implicit operator TService(LifecycleFakeHandle<TService> handle) => handle.Service;
    }

    /// <summary>Failure/observation configuration for a lifecycle fake.</summary>
    public sealed class FakeBehavior
    {
        internal int InitFailCount { get; private set; }
        internal Func<int, Exception>? InitializeExceptionFactory { get; private set; }
        internal int DisposeFailCount { get; private set; }
        internal TimeSpan InitDelay { get; private set; }

        /// <summary>The first N InitializeAsync attempts fail; attempt N+1 succeeds.</summary>
        public FakeBehavior FailInitializeAttempts(int attempts, Func<int, Exception>? exceptionFactory = null)
        {
            if (attempts < 0) throw new ArgumentOutOfRangeException(nameof(attempts));
            InitFailCount = attempts;
            InitializeExceptionFactory = exceptionFactory ?? (attempt =>
                new InvalidOperationException($"LifecycleFake: initialize attempt {attempt} configured to fail."));
            return this;
        }

        /// <summary>The first N DisposeAsync attempts fail; attempt N+1 succeeds.</summary>
        public FakeBehavior FailDisposeAttempts(int attempts)
        {
            if (attempts < 0) throw new ArgumentOutOfRangeException(nameof(attempts));
            DisposeFailCount = attempts;
            return this;
        }

        /// <summary>Adds a delay to every InitializeAsync attempt (uses Task.Delay; keep tiny in tests).</summary>
        public FakeBehavior WithInitializeDelay(TimeSpan delay)
        {
            if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
            InitDelay = delay;
            return this;
        }
    }

    /// <summary>Call log of a lifecycle fake, exposed for assertions.</summary>
    public sealed class FakeInvocationLog
    {
        private readonly List<string> _entries = new();

        public IReadOnlyList<string> Invocations => _entries;

        public bool WasInvoked(string methodName)
        {
            lock (_entries) return _entries.Exists(entry => entry == methodName || entry.StartsWith(methodName + "#", StringComparison.Ordinal));
        }

        internal void Record(string entry)
        {
            lock (_entries) _entries.Add(entry);
        }
    }

    internal sealed class FakeState
    {
        public object? Stub { get; set; }
        public FakeBehavior Behavior { get; } = new();
        public FakeInvocationLog Log { get; } = new();
        public int InitializeAttempts;
        public int DisposeAttempts;
    }

    public class FakeDispatchProxy : DispatchProxy
    {
        private readonly FakeState _state = new();

        public FakeDispatchProxy Bind(object? stub, FakeBehavior behavior)
        {
            _state.Stub = stub;
            Apply(behavior);
            return this;
        }

        // Behavior is immutable after configure; copy fields onto the state's own behavior.
        private void Apply(FakeBehavior behavior)
        {
            if (behavior.InitFailCount > 0)
                _state.Behavior.FailInitializeAttempts(behavior.InitFailCount, behavior.InitializeExceptionFactory);
            if (behavior.DisposeFailCount > 0)
                _state.Behavior.FailDisposeAttempts(behavior.DisposeFailCount);
            if (behavior.InitDelay > TimeSpan.Zero)
                _state.Behavior.WithInitializeDelay(behavior.InitDelay);
        }

        public FakeInvocationLog Log => _state.Log;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null) throw new ArgumentNullException(nameof(targetMethod));

            if (IsInitializeAsync(targetMethod))
                return HandleInitializeAsync(args);

            if (targetMethod.Name == "DisposeAsync" && targetMethod.ReturnType.Name.StartsWith("ValueTask", StringComparison.Ordinal))
                return HandleValueTaskDispose();

            if (targetMethod.Name == nameof(IDisposable.Dispose) && targetMethod.GetParameters().Length == 0)
                return HandleSyncDispose(targetMethod);

            _state.Log.Record(targetMethod.Name);
            return InvokeStub(targetMethod, args);
        }

        private static bool IsInitializeAsync(MethodInfo method)
            => method.Name == "InitializeAsync"
               && method.ReturnType == typeof(Task)
               && method.GetParameters().Length == 1
               && method.GetParameters()[0].ParameterType == typeof(CancellationToken);

        private object? HandleInitializeAsync(object?[]? args)
        {
            var attempt = Interlocked.Increment(ref _state.InitializeAttempts);
            var cancellationToken = args is { Length: > 0 } ? (CancellationToken)args[0]! : default;
            _state.Log.Record($"initialize#{attempt}");
            return RunInitializeAsync(cancellationToken);
        }

        private async Task RunInitializeAsync(CancellationToken cancellationToken)
        {
            if (_state.Behavior.InitDelay > TimeSpan.Zero)
                await Task.Delay(_state.Behavior.InitDelay, cancellationToken).ConfigureAwait(false);

            if (_state.InitializeAttempts <= _state.Behavior.InitFailCount)
                throw _state.Behavior.InitializeExceptionFactory!(_state.InitializeAttempts);

            await InvokeStubLifecycleAsync("InitializeAsync", cancellationToken).ConfigureAwait(false);
        }

        private object? HandleValueTaskDispose()
        {
            var attempt = Interlocked.Increment(ref _state.DisposeAttempts);
            _state.Log.Record($"disposeAsync#{attempt}");
            if (attempt <= _state.Behavior.DisposeFailCount)
                throw new InvalidOperationException($"LifecycleFake: dispose attempt {attempt} configured to fail.");
            return new ValueTask();
        }

        private object? HandleSyncDispose(MethodInfo targetMethod)
        {
            _state.Log.Record("dispose");
            return InvokeStub(targetMethod, Array.Empty<object?>());
        }

        private async Task InvokeStubLifecycleAsync(string methodName, CancellationToken cancellationToken)
        {
            var stub = _state.Stub;
            if (stub == null) return;
            var method = stub.GetType().GetMethod(methodName, new[] { typeof(CancellationToken) });
            if (method == null) return;
            var result = method.Invoke(stub, new object?[] { cancellationToken });
            if (result is Task task)
                await task.ConfigureAwait(false);
        }

        private object? InvokeStub(MethodInfo targetMethod, object?[]? args)
        {
            var stub = _state.Stub;
            if (stub == null)
            {
                var returnType = targetMethod.ReturnType;
                return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
            }

            try
            {
                return targetMethod.Invoke(stub, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
