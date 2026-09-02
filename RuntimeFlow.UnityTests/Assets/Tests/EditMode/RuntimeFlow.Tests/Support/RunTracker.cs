using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VContainer;

namespace RuntimeFlow.Tests.Support
{
    /// <summary>
    /// Keeps the runs and containers a fixture creates so a <c>[TearDown]</c> can dispose them all, instead
    /// of leaving cancellation sources, stall-watch loops and container-owned services behind for the rest
    /// of the editor session.
    /// </summary>
    public sealed class RunTracker
    {
        private readonly List<ScopeRun> _runs = new List<ScopeRun>();
        private readonly List<IObjectResolver> _containers = new List<IObjectResolver>();

        /// <summary>Builds a root container from an installer and tracks it.</summary>
        public IObjectResolver Build(Action<IContainerBuilder> install) => Track(TestScope.Build(install));

        /// <summary>Creates a run over <paramref name="scope"/> and tracks it.</summary>
        public ScopeRun Create(
            IObjectResolver scope,
            string name,
            RuntimeFlowOptions options,
            IReadOnlyList<ScopeRun>? parents = null,
            bool ownsScope = false)
            => Track(ScopeRun.Create(scope, name, options, parents, ownsScope));

        /// <summary>Tracks a run created elsewhere; returns it so calls can be chained.</summary>
        public ScopeRun Track(ScopeRun run)
        {
            _runs.Add(run);
            return run;
        }

        /// <summary>Tracks a container created elsewhere; returns it so calls can be chained.</summary>
        public IObjectResolver Track(IObjectResolver container)
        {
            _containers.Add(container);
            return container;
        }

        /// <summary>
        /// Disposes everything, newest first. Teardown never blocks: waiting on a disposal whose
        /// continuations are posted back to the editor's synchronization context would deadlock the very
        /// thread that has to pump them, so a disposal that cannot finish synchronously is left to finish
        /// on the editor loop with its failures observed.
        /// </summary>
        public void DisposeAll()
        {
            for (var i = _runs.Count - 1; i >= 0; i--)
            {
                try
                {
                    Observe(_runs[i].DisposeAsync());
                }
                catch (Exception)
                {
                    // Teardown never fails a test that already made its assertions.
                }
            }
            _runs.Clear();

            for (var i = _containers.Count - 1; i >= 0; i--)
            {
                try
                {
                    _containers[i].Dispose();
                }
                catch (Exception)
                {
                }
            }
            _containers.Clear();
        }

        private static void Observe(ValueTask disposal)
        {
            if (disposal.IsCompletedSuccessfully) return;
            _ = disposal.AsTask().ContinueWith(
                finished => _ = finished.Exception, TaskContinuationOptions.ExecuteSynchronously);
        }
    }
}
