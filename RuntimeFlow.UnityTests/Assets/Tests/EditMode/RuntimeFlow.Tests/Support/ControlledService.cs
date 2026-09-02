using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VContainer;

namespace RuntimeFlow.Tests.Support
{
    /// <summary>
    /// An <see cref="IAsyncInitializable"/> whose initialization is driven by explicit gates instead of
    /// timing: await <see cref="Started"/> to know it is in flight, then <see cref="Release"/> it.
    /// Deterministic by construction, so tests never race.
    /// </summary>
    public abstract class ControlledService : IAsyncInitializable
    {
        private readonly TaskCompletionSource<bool> _started =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<bool> _gate =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes as soon as InitializeAsync is entered.</summary>
        public Task Started => _started.Task;

        /// <summary>Number of times InitializeAsync was entered.</summary>
        public int Attempts { get; private set; }

        /// <summary>When set, InitializeAsync throws it after the gate opens (or immediately when auto-completing).</summary>
        public Exception? Throw { get; set; }

        /// <summary>When true InitializeAsync returns without waiting for <see cref="Release"/>.</summary>
        public bool AutoComplete { get; set; }

        /// <summary>The context handed to the last InitializeAsync call.</summary>
        public InitContext? Context { get; private set; }

        /// <summary>The token handed to the last InitializeAsync call.</summary>
        public CancellationToken Token { get; private set; }

        /// <summary>True once InitializeAsync returned successfully.</summary>
        public bool Finished { get; private set; }

        /// <summary>Opens the gate so a waiting InitializeAsync can finish.</summary>
        public void Release() => _gate.TrySetResult(true);

        /// <summary>Opens the gate with a failure, so InitializeAsync throws it.</summary>
        public void Fail(Exception error) => _gate.TrySetException(error);

        /// <inheritdoc />
        public async Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
        {
            Attempts++;
            Context = context;
            Token = cancellationToken;
            _started.TrySetResult(true);

            if (!AutoComplete)
            {
                using (cancellationToken.Register(() => _gate.TrySetCanceled()))
                {
                    await _gate.Task;
                }
            }

            if (Throw != null) throw Throw;
            Finished = true;
        }
    }

    /// <summary>A <see cref="ControlledService"/> that completes as soon as it is started.</summary>
    public abstract class AutoService : ControlledService
    {
        /// <summary>Creates a service that does not wait for a gate.</summary>
        protected AutoService() => AutoComplete = true;
    }

    /// <summary>Shared container and option plumbing for the RuntimeFlow test suite.</summary>
    public static class TestScope
    {
        /// <summary>Options with a capturing logger, no timeouts and no stall warnings by default.</summary>
        public static RuntimeFlowOptions Options(CapturingLogger logger, params IRuntimeFlowObserver[] observers)
        {
            var options = new RuntimeFlowOptions
            {
                Logger = logger,
                TimeoutMultiplier = 0,
                StallWarningAfter = TimeSpan.Zero,
                CancellationGrace = TimeSpan.FromMilliseconds(200)
            };
            foreach (var observer in observers) options.Observers.Add(observer);
            return options;
        }

        /// <summary>Builds a root container from an installer.</summary>
        public static IObjectResolver Build(Action<IContainerBuilder> install)
        {
            var builder = new ContainerBuilder();
            install(builder);
            return builder.Build();
        }

        /// <summary>Registers a service as itself and as every interface it implements.</summary>
        public static RegistrationBuilder Add<T>(this IContainerBuilder builder) where T : class, IAsyncInitializable
            => builder.Register<T>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces();

        /// <summary>Names of the services in a status snapshot that are in the given state.</summary>
        public static List<string> Names(this RuntimeFlowStatus status, ServiceState state)
        {
            var names = new List<string>();
            foreach (var service in status.Services)
            {
                if (service.State == state) names.Add(service.Name);
            }
            return names;
        }

        /// <summary>The snapshot of one service by name.</summary>
        public static ServiceStatus Service(this RuntimeFlowStatus status, string name)
        {
            foreach (var service in status.Services)
            {
                if (service.Name == name) return service;
            }
            throw new KeyNotFoundException($"No service named '{name}' in {status.Scope}.");
        }
    }
}
