using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{
    /// <summary>
    /// Behavioral coverage for the built-in flow scenarios and presets:
    /// InitializeOnly, EnsureSceneLoadedThenInitialize, StandardSession,
    /// and the RuntimePipelinePresets option bundles.
    /// </summary>
    public sealed class RuntimeFlowScenarioTests
    {
        private sealed class RecordingFlowContext : IRuntimeFlowContext
        {
            public List<string> Calls { get; } = new();
            public IGameContext SessionContext { get; } = new GameContext();
            public Func<Type, Task>? OnLoadScopeSceneAsync { get; set; }
            public SceneRoute? LastResolvedFallbackRoute { get; private set; }

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                Calls.Add("initialize");
                return Task.CompletedTask;
            }

            public Task LoadScopeSceneAsync(Type sceneScopeKey, CancellationToken cancellationToken = default)
            {
                Calls.Add($"load-scene:{sceneScopeKey.Name}");
                return OnLoadScopeSceneAsync != null ? OnLoadScopeSceneAsync(sceneScopeKey) : Task.CompletedTask;
            }

            public Task LoadScopeSceneAsync<TSceneScope>(CancellationToken cancellationToken = default) => LoadScopeSceneAsync(typeof(TSceneScope), cancellationToken);
            public Task LoadScopeModuleAsync(Type moduleScopeKey, CancellationToken cancellationToken = default) { Calls.Add($"load-module:{moduleScopeKey.Name}"); return Task.CompletedTask; }
            public Task LoadScopeModuleAsync<TModuleScope>(CancellationToken cancellationToken = default) => LoadScopeModuleAsync(typeof(TModuleScope), cancellationToken);
            public Task ReloadScopeModuleAsync(Type moduleScopeKey, CancellationToken cancellationToken = default) { Calls.Add($"reload-module:{moduleScopeKey.Name}"); return Task.CompletedTask; }
            public Task ReloadScopeModuleAsync<TModuleScope>(CancellationToken cancellationToken = default) => ReloadScopeModuleAsync(typeof(TModuleScope), cancellationToken);
            public Task ReloadScopeAsync(Type scopeType, CancellationToken cancellationToken = default) { Calls.Add($"reload:{scopeType.Name}"); return Task.CompletedTask; }
            public Task ReloadScopeAsync<TScope>(CancellationToken cancellationToken = default) => ReloadScopeAsync(typeof(TScope), cancellationToken);
            public Task LoadSceneSingleAsync(string sceneName, CancellationToken cancellationToken = default) { Calls.Add($"load-single:{sceneName}"); return Task.CompletedTask; }
            public Task LoadSceneAdditiveAsync(string sceneName, CancellationToken cancellationToken = default) { Calls.Add($"load-additive:{sceneName}"); return Task.CompletedTask; }
            public Task GoToAsync(SceneRoute route, CancellationToken cancellationToken = default) { Calls.Add($"goto:{route.SceneName}"); return Task.CompletedTask; }

            public Task<SceneRoute> ResolveRouteAsync(SceneRoute fallbackRoute, ISessionSceneRouteResolver? routeResolver = null, CancellationToken cancellationToken = default)
            {
                LastResolvedFallbackRoute = fallbackRoute;
                Calls.Add($"resolve-route:{fallbackRoute.SceneName}");
                return Task.FromResult(fallbackRoute);
            }

            public TService ResolveSessionService<TService>() where TService : class => throw new InvalidOperationException("Not expected in scenario tests.");
            public bool TryResolveSessionService<TService>(out TService? service) where TService : class { service = null; return false; }
            public Task PreloadSceneAsync<TSceneScope>(CancellationToken cancellationToken = default) { Calls.Add($"preload-scene:{typeof(TSceneScope).Name}"); return Task.CompletedTask; }
            public Task PreloadModuleAsync<TModuleScope>(CancellationToken cancellationToken = default) { Calls.Add($"preload-module:{typeof(TModuleScope).Name}"); return Task.CompletedTask; }
            public bool HasPreloadedScope<TScope>() => false;
            public Task LoadAdditiveModuleAsync<TModuleScope>(CancellationToken cancellationToken = default) { Calls.Add($"load-additive-module:{typeof(TModuleScope).Name}"); return Task.CompletedTask; }
            public Task UnloadAdditiveModuleAsync<TModuleScope>(CancellationToken cancellationToken = default) { Calls.Add($"unload-additive-module:{typeof(TModuleScope).Name}"); return Task.CompletedTask; }
        }

        [Test]
        public async Task InitializeOnly_InitializesWithoutSceneOperations()
        {
            var context = new RecordingFlowContext();

            await RuntimeFlowPresets.InitializeOnly().ExecuteAsync(context);

            CollectionAssert.AreEqual(new[] { "initialize" }, context.Calls);
        }

        [Test]
        public void EnsureSceneLoadedThenInitialize_NullScene_Throws()
        {
            Assert.Throws<ArgumentException>(() => RuntimeFlowPresets.EnsureSceneLoadedThenInitialize(" "));
        }

        [Test]
        public async Task EnsureSceneLoadedThenInitialize_LoadsSceneThenInitializes()
        {
            var context = new RecordingFlowContext();

            await RuntimeFlowPresets.EnsureSceneLoadedThenInitialize("Session").ExecuteAsync(context);

            CollectionAssert.AreEqual(new[] { "load-additive:Session", "initialize" }, context.Calls);
        }

        [Test]
        public async Task StandardSession_ResolvesFallbackRouteAndGoesToIt()
        {
            var context = new RecordingFlowContext();
            var fallback = SceneRoute.ToScene<TestSceneScope>("Fallback");

            await RuntimeFlowPresets.StandardSession(fallback).ExecuteAsync(context);

            Assert.AreSame(fallback, context.LastResolvedFallbackRoute);
            CollectionAssert.AreEqual(new[] { "initialize", "resolve-route:Fallback", "goto:Fallback" }, context.Calls);
        }

        [Test]
        public void StandardSession_NullRoute_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => RuntimeFlowPresets.StandardSession(null!));
        }

        [Test]
        public async Task Pipeline_RunAsync_ExecutesConfiguredScenario()
        {
            IRuntimeFlowContext? observedContext = null;
            var pipeline = RuntimePipeline.Create(
                builder =>
                {
                    builder.DefineGlobalScope();
                    builder.DefineSessionScope();
                },
                configureOptions: options => options.ReplayFlowOnSessionRestart = false);
            pipeline.ConfigureFlow(new DelegateRuntimeFlowScenario((context, _) => { observedContext = context; return Task.CompletedTask; }));

            try
            {
                await pipeline.RunAsync(new NoopSceneLoader());

                Assert.IsNotNull(observedContext);
            }
            finally
            {
                await pipeline.DisposeAsync();
            }
        }

        [Test]
        public async Task Pipeline_WithoutFlow_RunAsync_ThrowsFlowNotConfigured()
        {
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.DefineGlobalScope();
                builder.DefineSessionScope();
            });
            try
            {
                Assert.ThrowsAsync<FlowNotConfiguredException>(async () => await pipeline.RunAsync(new NoopSceneLoader()));
            }
            finally
            {
                await pipeline.DisposeAsync();
            }
        }

        [Test]
        public async Task ProductionPreset_EnablesHealthAndRetry()
        {
            RuntimePipelineOptions? captured = null;
            var pipeline = RuntimePipeline.Create(
                builder =>
                {
                    builder.DefineGlobalScope();
                    builder.DefineSessionScope();
                },
                configureOptions: options =>
                {
                    RuntimePipelinePresets.Production(options);
                    captured = options;
                });
            try
            {
                Assert.IsNotNull(captured);
                Assert.IsTrue(captured!.Health.Enabled);
                Assert.AreEqual(2, captured.RetryPolicy.MaxAttempts);
                await pipeline.InitializeAsync();
            }
            finally
            {
                await pipeline.DisposeAsync();
            }
        }

        [Test]
        public async Task DevelopmentPreset_DisablesHealthAndRetry()
        {
            RuntimePipelineOptions? captured = null;
            var pipeline = RuntimePipeline.Create(
                builder =>
                {
                    builder.DefineGlobalScope();
                    builder.DefineSessionScope();
                },
                configureOptions: options =>
                {
                    RuntimePipelinePresets.Development(options);
                    captured = options;
                });
            try
            {
                Assert.IsNotNull(captured);
                Assert.IsFalse(captured!.Health.Enabled);
                Assert.AreEqual(0, captured.RetryPolicy.MaxAttempts);
                await pipeline.InitializeAsync();
            }
            finally
            {
                await pipeline.DisposeAsync();
            }
        }
    }
}
