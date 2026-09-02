using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Initialization.Planning
{
    /// <summary>
    /// A player-interaction gate inside the load graph: a node implementing
    /// <c>IUserInteractionGatedInitializableService</c> awaits <see cref="IUserGate.WaitAsync"/>
    /// in its InitializeAsync while the pipeline reports an awaiting-player-input status.
    /// The game completes the gate through a <see cref="UserGateCompleter"/> resolved from DI
    /// (or directly) when the player answers the dialog.
    /// </summary>
    public interface IUserGate
    {
        /// <summary>Human-readable description of what the player is asked (analytics, UI).</summary>
        string Prompt { get; }

        /// <summary>Completes when the player answered; canceled with the startup token.</summary>
        Task WaitAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Completes user gates. The game resolves this from any scope and calls
    /// <see cref="Complete"/> when the player confirms; tests call it to simulate the player.
    /// </summary>
    public sealed class UserGateCompleter
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> _gates = new(StringComparer.Ordinal);

        public IUserGate Create(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                throw new ArgumentException("Prompt is required.", nameof(prompt));
            return new UserGate(this, prompt);
        }

        /// <summary>Releases every waiter registered under <paramref name="prompt"/>.</summary>
        public bool Complete(string prompt)
        {
            if (_gates.TryRemove(prompt, out var tcs))
                return tcs.TrySetResult(true);
            return false;
        }

        internal Task WaitAsync(UserGate gate, CancellationToken cancellationToken)
        {
            var tcs = _gates.GetOrAdd(gate.Prompt, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            if (cancellationToken.CanBeCanceled)
            {
                var state = new CancelState(this, gate.Prompt);
                var registration = cancellationToken.Register(static obj =>
                {
                    var s = (CancelState)obj!;
                    if (s.Owner._gates.TryRemove(s.Prompt, out var pending))
                        pending.TrySetCanceled();
                }, state);
                return AwaitWithCleanupAsync(tcs.Task, registration);
            }
            return tcs.Task;
        }

        private sealed class CancelState
        {
            public CancelState(UserGateCompleter owner, string prompt)
            {
                Owner = owner;
                Prompt = prompt;
            }

            public UserGateCompleter Owner { get; }
            public string Prompt { get; }
        }

        private static async Task AwaitWithCleanupAsync(Task<bool> task, CancellationTokenRegistration registration)
        {
            try { await task.ConfigureAwait(false); }
            finally { registration.Dispose(); }
        }

        public sealed class UserGate : IUserGate
        {
            private readonly UserGateCompleter _owner;
            public string Prompt { get; }

            internal UserGate(UserGateCompleter owner, string prompt)
            {
                _owner = owner;
                Prompt = prompt;
            }

            public Task WaitAsync(CancellationToken cancellationToken)
                => _owner.WaitAsync(this, cancellationToken);
        }
    }
}
