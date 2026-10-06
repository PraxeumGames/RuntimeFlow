using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Testing
{
    /// <summary>
    /// Builds a dispatch-proxy fake around an optional stub that participates in the RuntimeFlow
    /// lifecycle: initialization can fail N times, be delayed, or hang until cancelled, disposal can be
    /// observed or made to fail, and every intercepted call is recorded.
    /// <code>
    /// interface IFakePayments : IPaymentsService, IAsyncInitializable, IAsyncDisposable { }
    /// var fake = LifecycleFake.Of&lt;IFakePayments&gt;(new StubPayments(), cfg =&gt; cfg.FailInitializeAttempts(2));
    /// </code>
    /// </summary>
    public static class LifecycleFake
    {
        /// <summary>Creates a fake implementing <typeparamref name="TService"/>, optionally backed by a stub.</summary>
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

    /// <summary>A fake and the log of everything that was called on it.</summary>
    public sealed class LifecycleFakeHandle<TService> where TService : class
    {
        internal LifecycleFakeHandle(TService service, FakeInvocationLog log)
        {
            Service = service;
            Log = log;
        }

        /// <summary>The proxy, ready to be registered in a container.</summary>
        public TService Service { get; }

        /// <summary>Every intercepted call, in order.</summary>
        public FakeInvocationLog Log { get; }

        /// <summary>Lets a handle be used wherever the service interface is expected.</summary>
        public static implicit operator TService(LifecycleFakeHandle<TService> handle) => handle.Service;
    }

    /// <summary>Failure, delay and observation configuration for a lifecycle fake.</summary>
    public sealed class FakeBehavior
    {
        internal int InitFailCount { get; private set; }
        internal Func<int, Exception>? InitializeExceptionFactory { get; private set; }
        internal int DisposeFailCount { get; private set; }
        internal TimeSpan InitDelay { get; private set; }
        internal bool Hangs { get; private set; }

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

        /// <summary>Delays every InitializeAsync attempt; keep it tiny in tests.</summary>
        public FakeBehavior DelayInitialize(TimeSpan delay)
        {
            if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
            InitDelay = delay;
            return this;
        }

        /// <summary>InitializeAsync never returns until its cancellation token fires.</summary>
        public FakeBehavior Hang()
        {
            Hangs = true;
            return this;
        }
    }

    /// <summary>Call log of a lifecycle fake, exposed for assertions.</summary>
    public sealed class FakeInvocationLog
    {
        private readonly List<string> _entries = new List<string>();

        /// <summary>Recorded calls in order; initialization entries look like "initialize#1".</summary>
        public IReadOnlyList<string> Invocations => _entries;

        /// <summary>True when a method with that name (or an attempt-numbered variant) was called.</summary>
        public bool WasInvoked(string methodName)
            => _entries.Exists(entry => entry == methodName || entry.StartsWith(methodName + "#", StringComparison.Ordinal));

        internal void Record(string entry) => _entries.Add(entry);
    }

    internal sealed class FakeState
    {
        public object? Stub { get; set; }
        public FakeBehavior Behavior { get; set; } = new FakeBehavior();
        public FakeInvocationLog Log { get; } = new FakeInvocationLog();
        public int InitializeAttempts;
        public int DisposeAttempts;
    }

    /// <summary>The DispatchProxy base that implements the fake's behaviour; created by <see cref="LifecycleFake"/>.</summary>
    public class FakeDispatchProxy : DispatchProxy
    {
        private readonly FakeState _state = new FakeState();

        /// <summary>Binds the optional stub and the configured behaviour to this proxy.</summary>
        public FakeDispatchProxy Bind(object? stub, FakeBehavior behavior)
        {
            _state.Stub = stub;

            // The configured behaviour is taken as it is instead of being replayed through its own
            // setters: replaying dropped everything whose value happened to be the default, so
            // FailInitializeAttempts(0) — "never fail, but use my exception factory" — was silently lost.
            _state.Behavior = behavior ?? new FakeBehavior();
            return this;
        }

        /// <summary>The proxy's invocation log.</summary>
        public FakeInvocationLog Log => _state.Log;

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null) throw new ArgumentNullException(nameof(targetMethod));

            if (IsInitializeAsync(targetMethod))
                return HandleInitializeAsync(args);

            if (targetMethod.Name == "DisposeAsync" && targetMethod.ReturnType.Name.StartsWith("ValueTask", StringComparison.Ordinal))
                return HandleValueTaskDispose(targetMethod);

            if (targetMethod.Name == nameof(IDisposable.Dispose) && targetMethod.GetParameters().Length == 0)
                return HandleSyncDispose(targetMethod);

            _state.Log.Record(targetMethod.Name);
            return InvokeStub(targetMethod, args);
        }

        private static bool IsInitializeAsync(MethodInfo method)
        {
            if (method.Name != "InitializeAsync" || method.ReturnType != typeof(Task)) return false;
            var parameters = method.GetParameters();
            return parameters.Length == 2
                   && parameters[0].ParameterType == typeof(InitContext)
                   && parameters[1].ParameterType == typeof(CancellationToken);
        }

        private object HandleInitializeAsync(object?[]? args)
        {
            var attempt = ++_state.InitializeAttempts;
            var context = args is { Length: > 0 } ? args[0] as InitContext : null;
            var cancellationToken = args is { Length: > 1 } ? (CancellationToken)args[1]! : default;
            _state.Log.Record($"initialize#{attempt}");
            return RunInitializeAsync(context, cancellationToken);
        }

        private async Task RunInitializeAsync(InitContext? context, CancellationToken cancellationToken)
        {
            if (_state.Behavior.InitDelay > TimeSpan.Zero)
                await Task.Delay(_state.Behavior.InitDelay, cancellationToken);

            if (_state.Behavior.Hangs)
                await Task.Delay(Timeout.Infinite, cancellationToken);

            if (_state.InitializeAttempts <= _state.Behavior.InitFailCount)
                throw _state.Behavior.InitializeExceptionFactory!(_state.InitializeAttempts);

            await InvokeStubInitializeAsync(context, cancellationToken);
        }

        private object HandleValueTaskDispose(MethodInfo targetMethod)
        {
            var attempt = ++_state.DisposeAttempts;
            _state.Log.Record($"disposeAsync#{attempt}");
            return DisposeStubAsync(targetMethod, attempt);
        }

        /// <summary>
        /// Fails the configured number of attempts through the returned <see cref="ValueTask"/> — a
        /// synchronous throw would escape the caller's await — then forwards to the stub like every other
        /// intercepted call, so a stub can observe its own disposal.
        /// </summary>
        private async ValueTask DisposeStubAsync(MethodInfo targetMethod, int attempt)
        {
            if (attempt <= _state.Behavior.DisposeFailCount)
                throw new InvalidOperationException($"LifecycleFake: dispose attempt {attempt} configured to fail.");

            switch (InvokeStub(targetMethod, Array.Empty<object?>()))
            {
                case ValueTask pending:
                    await pending;
                    break;
                case Task task:
                    await task;
                    break;
            }
        }

        private object? HandleSyncDispose(MethodInfo targetMethod)
        {
            _state.Log.Record("dispose");
            return InvokeStub(targetMethod, Array.Empty<object?>());
        }

        private async Task InvokeStubInitializeAsync(InitContext? context, CancellationToken cancellationToken)
        {
            var stub = _state.Stub;
            if (stub == null) return;
            // Interface dispatch covers both implicit and explicit implementations: an explicit
            // `Task IAsyncInitializable.InitializeAsync(...)` is invisible to GetMethod by name and
            // the old lookup silently skipped the stub entirely.
            if (stub is IAsyncInitializable initializable)
            {
                await initializable.InitializeAsync(context!, cancellationToken);
                return;
            }
            var method = stub.GetType().GetMethod("InitializeAsync", new[] { typeof(InitContext), typeof(CancellationToken) });
            if (method == null) return;
            if (method.Invoke(stub, new object?[] { context, cancellationToken }) is Task task)
                await task;
        }

        /// <summary>
        /// What a stub-less member returns: a completed task for <see cref="Task"/> and <c>Task&lt;T&gt;</c>
        /// (awaiting null would throw), the default for value types, null otherwise.
        /// </summary>
        private static object? DefaultResult(Type returnType)
        {
            if (returnType == typeof(void)) return null;
            if (returnType == typeof(Task)) return Task.CompletedTask;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var result = returnType.GetGenericArguments()[0];
                var value = result.IsValueType ? Activator.CreateInstance(result) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, new[] { value });
            }
            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }

        private object? InvokeStub(MethodInfo targetMethod, object?[]? args)
        {
            var stub = _state.Stub;
            if (stub == null) return DefaultResult(targetMethod.ReturnType);

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
