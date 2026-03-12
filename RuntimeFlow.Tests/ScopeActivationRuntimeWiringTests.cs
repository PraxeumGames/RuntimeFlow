using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

public sealed class ScopeActivationRuntimeWiringTests
{
    [Fact]
    public async Task InitializeAndLoadScopes_ExecutesEnterHooksInScopeOrder()
    {
        var calls = new List<string>();
        var pipeline = CreatePipeline(calls);

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadModuleAsync<TestModuleScope>();

        Assert.Equal(
            new[]
            {
                "enter:session:alpha",
                "enter:session:beta",
                "enter:scene:alpha",
                "enter:scene:beta",
                "enter:module:alpha",
                "enter:module:beta"
            },
            calls);
    }

    [Fact]
    public async Task ReloadSceneAsync_ExecutesModuleThenSceneExitHooksBeforeSceneEnterHooks()
    {
        var calls = new List<string>();
        var pipeline = CreatePipeline(calls);

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadModuleAsync<TestModuleScope>();

        calls.Clear();
        await pipeline.ReloadScopeAsync<TestSceneScope>();

        Assert.Equal(
            new[]
            {
                "exit:module:beta",
                "exit:module:alpha",
                "exit:scene:beta",
                "exit:scene:alpha",
                "enter:scene:alpha",
                "enter:scene:beta"
            },
            calls);
    }

    [Fact]
    public async Task ReloadModuleAsync_ExecutesModuleExitHooksBeforeModuleEnterHooks()
    {
        var calls = new List<string>();
        var pipeline = CreatePipeline(calls);

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadModuleAsync<TestModuleScope>();

        calls.Clear();
        await pipeline.ReloadScopeAsync<TestModuleScope>();

        Assert.Equal(
            new[]
            {
                "exit:module:beta",
                "exit:module:alpha",
                "enter:module:alpha",
                "enter:module:beta"
            },
            calls);
    }

    [Fact]
    public async Task RestartSessionAsync_ExecutesModuleSceneSessionExitHooksThenReentersScopes()
    {
        var calls = new List<string>();
        var pipeline = CreatePipeline(calls);

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadModuleAsync<TestModuleScope>();

        calls.Clear();
        await pipeline.RestartSessionAsync();

        Assert.Equal(
            new[]
            {
                "exit:module:beta",
                "exit:module:alpha",
                "exit:scene:beta",
                "exit:scene:alpha",
                "exit:session:beta",
                "exit:session:alpha",
                "enter:session:alpha",
                "enter:session:beta",
                "enter:scene:alpha",
                "enter:scene:beta",
                "enter:module:alpha",
                "enter:module:beta"
            },
            calls);
    }

    private static RuntimePipeline CreatePipeline(List<string> calls)
    {
        return RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Session().RegisterInstance<IAlphaSessionActivationService>(new AlphaSessionActivationService(calls));
            builder.Session().RegisterInstance<IBetaSessionActivationService>(new BetaSessionActivationService(calls));
            builder.Scene(new TestSceneScope(s => {
                s.RegisterInstance<IAlphaSceneActivationService>(new AlphaSceneActivationService(calls));
                s.RegisterInstance<IBetaSceneActivationService>(new BetaSceneActivationService(calls));
            }));
            builder.Module(new TestModuleScope(m => {
                m.RegisterInstance<IAlphaModuleActivationService>(new AlphaModuleActivationService(calls));
                m.RegisterInstance<IBetaModuleActivationService>(new BetaModuleActivationService(calls));
            }));
        });
    }

    private interface IAlphaSessionActivationService : ISessionInitializableService, ISessionScopeActivationService;
    private interface IBetaSessionActivationService : ISessionInitializableService, ISessionScopeActivationService;
    private interface IAlphaSceneActivationService : ISceneInitializableService, ISceneScopeActivationService;
    private interface IBetaSceneActivationService : ISceneInitializableService, ISceneScopeActivationService;
    private interface IAlphaModuleActivationService : IModuleInitializableService, IModuleScopeActivationService;
    private interface IBetaModuleActivationService : IModuleInitializableService, IModuleScopeActivationService;

    private abstract class ActivationServiceBase : IAsyncInitializableService, IAsyncScopeActivationService
    {
        private readonly List<string> _calls;
        private readonly string _scope;
        private readonly string _name;

        protected ActivationServiceBase(List<string> calls, string scope, string name)
        {
            _calls = calls;
            _scope = scope;
            _name = name;
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task OnScopeActivatedAsync(CancellationToken cancellationToken)
        {
            _calls.Add($"enter:{_scope}:{_name}");
            return Task.CompletedTask;
        }

        public Task OnScopeDeactivatingAsync(CancellationToken cancellationToken)
        {
            _calls.Add($"exit:{_scope}:{_name}");
            return Task.CompletedTask;
        }
    }

    private sealed class AlphaSessionActivationService : ActivationServiceBase, IAlphaSessionActivationService
    {
        public AlphaSessionActivationService(List<string> calls) : base(calls, "session", "alpha")
        {
        }
    }

    private sealed class BetaSessionActivationService : ActivationServiceBase, IBetaSessionActivationService
    {
        public BetaSessionActivationService(List<string> calls) : base(calls, "session", "beta")
        {
        }
    }

    private sealed class AlphaSceneActivationService : ActivationServiceBase, IAlphaSceneActivationService
    {
        public AlphaSceneActivationService(List<string> calls) : base(calls, "scene", "alpha")
        {
        }
    }

    private sealed class BetaSceneActivationService : ActivationServiceBase, IBetaSceneActivationService
    {
        public BetaSceneActivationService(List<string> calls) : base(calls, "scene", "beta")
        {
        }
    }

    private sealed class AlphaModuleActivationService : ActivationServiceBase, IAlphaModuleActivationService
    {
        public AlphaModuleActivationService(List<string> calls) : base(calls, "module", "alpha")
        {
        }
    }

    private sealed class BetaModuleActivationService : ActivationServiceBase, IBetaModuleActivationService
    {
        public BetaModuleActivationService(List<string> calls) : base(calls, "module", "beta")
        {
        }
    }
}
