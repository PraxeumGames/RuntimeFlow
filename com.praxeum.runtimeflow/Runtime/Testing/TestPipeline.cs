using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using RuntimeFlow.Pipeline;

namespace RuntimeFlow.Testing
{
    /// <summary>Registers a pre-built fake/stub instance for <typeparamref name="TService"/>.</summary>
    public static class TestPipelineBuilderExtensions
    {
        public static TestPipelineBuilder Override<TService>(this TestPipelineBuilder builder, TService instance)
            where TService : class
            => builder.OverrideInstance(instance);
    }

    /// <summary>
    /// Deterministic, fast pipeline harness for tests: inline scheduling, health supervision
    /// disabled, fluent dependency overrides with typo protection, and first-class activation
    /// of <see cref="RuntimePipeline.ActivePipeline"/>.
    /// </summary>
    public sealed class TestPipeline : IAsyncDisposable
    {
        private readonly RuntimePipeline _pipeline;
        private readonly List<Func<IGameContext, Task>> _postBuildChecks;

        internal TestPipeline(RuntimePipeline pipeline, List<Func<IGameContext, Task>> postBuildChecks)
        {
            _pipeline = pipeline;
            _postBuildChecks = postBuildChecks;
        }

        public RuntimePipeline Pipeline => _pipeline;
        public IGameContext SessionContext => _pipeline.SessionContext;

        /// <summary>Starts building a test pipeline. Call <see cref="StartAsync"/> to initialize.</summary>
        public static TestPipelineBuilder Create(Action<IGameContextBuilder>? configure = null)
            => new TestPipelineBuilder(configure);

        /// <summary>
        /// Activates this pipeline as <see cref="RuntimePipeline.ActivePipeline"/> so code
        /// paths that resolve the ambient pipeline (dashboard bridge, restart handlers) work
        /// under test. Disposal clears the activation.
        /// </summary>
        public TestPipeline Activate()
        {
            RuntimePipeline.ActivePipeline = _pipeline;
            return this;
        }

        public async ValueTask DisposeAsync()
        {
            if (RuntimePipeline.ActivePipeline == _pipeline)
                RuntimePipeline.ActivePipeline = null;
            await _pipeline.DisposeAsync().ConfigureAwait(false);
        }
    }

    public sealed class TestPipelineBuilder
    {
        private Action<IGameContextBuilder>? _configure;
        private readonly List<Func<IGameContext, Task>> _postBuildChecks = new();

        internal TestPipelineBuilder(Action<IGameContextBuilder>? configure)
        {
            _configure = configure;
        }

        /// <summary>
        /// Replaces the registration of <typeparamref name="TService"/> with
        /// <typeparamref name="TOverride"/>. After the pipeline starts, the override is
        /// verified by resolution and type check — a typo'd interface fails the test with a
        /// descriptive message instead of silently running the real implementation.
        /// </summary>
        public TestPipelineBuilder Override<TService, TOverride>()
            where TService : class
            where TOverride : class, TService
        {
            _postBuildChecks.Add(async context =>
            {
                var resolved = await context.ResolveAsync<TService>().ConfigureAwait(false);
                if (resolved is not TOverride)
                    throw new InvalidOperationException(
                        $"Override did not take effect: expected '{typeof(TOverride).Name}' for service " +
                        $"'{typeof(TService).Name}', but resolved '{resolved?.GetType().Name ?? "<null>"}'. " +
                        "Check that the override is registered in the same scope that registers the service.");
            });
            _configure += builder => builder.Session().Register<TService, TOverride>(DiLifetime.Singleton);
            return this;
        }

        /// <summary>
        /// Replaces the registration of <typeparamref name="TService"/> with an instance.
        /// The instance is additionally exposed under every lifecycle contract its runtime
        /// type implements (initializable/disposable/activation markers), so instance-based
        /// fakes participate in startup discovery exactly like type-registered services.
        /// </summary>
        public TestPipelineBuilder OverrideInstance<TService>(TService instance, GameContextType scope = GameContextType.Session)
            where TService : class
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            _postBuildChecks.Add(async context =>
            {
                var resolved = await context.ResolveAsync<TService>().ConfigureAwait(false);
                if (!ReferenceEquals(resolved, instance))
                    throw new InvalidOperationException(
                        $"Override did not take effect: resolved '{resolved?.GetType().Name ?? "<null>"}' instead of " +
                        $"the provided instance for service '{typeof(TService).Name}'.");
            });
            _configure += builderConcrete => ((GameContextBuilder)builderConcrete).RegisterInstanceDeferredForDiscovery(
                scope, instance, typeof(TService));
            return this;
        }

        /// <summary>Runs additional configuration on the builder (scopes, real services, guards).</summary>
        public TestPipelineBuilder Configure(Action<IGameContextBuilder> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            _configure += configure;
            return this;
        }

        /// <summary>Builds and initializes the pipeline (global + session scopes).</summary>
        /// <summary>Hard deadline for StartAsync; hangs surface as TimeoutException. Default: 60 seconds.</summary>
        public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(60);

        public async Task<TestPipeline> StartAsync(CancellationToken cancellationToken = default)
        {
            var pipeline = RuntimePipeline.Create(
                builder =>
                {
                    builder.DefineGlobalScope();
                    builder.DefineSessionScope();
                    _configure?.Invoke(builder);
                },
                configureOptions: options =>
                {
                    options.ExecutionScheduler = InlineStrictInitializationExecutionScheduler.Instance;
                    // Fully deterministic: strict inline scheduling (immune to ambient
                    // main-thread captures), no supervision, no retries.
                    options.Health.Enabled = false;
                    options.RetryPolicy.MaxAttempts = 0;
                });

            try
            {
                var bootTask = pipeline.InitializeAsync(cancellationToken: cancellationToken);
                var timeoutTask = Task.Delay(StartupTimeout, CancellationToken.None);
                var completed = await Task.WhenAny(bootTask, timeoutTask).ConfigureAwait(false);

                if (completed != bootTask)
                {
                    var status = pipeline.GetRuntimeStatus();
                    throw new TimeoutException(
                        $"TestPipeline startup exceeded {StartupTimeout}. Last status: [{status.State}] " +
                        $"{status.CurrentOperationCode}: {status.Message}");
                }

                await bootTask.ConfigureAwait(false);
            }
            catch
            {
                var disposeTask = pipeline.DisposeAsync().AsTask();
                if (!disposeTask.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Pipeline disposal hung after startup failure.");
                throw;
            }

            var session = pipeline.SessionContext;
            foreach (var check in _postBuildChecks)
                await check(session).ConfigureAwait(false);

            return new TestPipeline(pipeline, _postBuildChecks);
        }
    }
}
