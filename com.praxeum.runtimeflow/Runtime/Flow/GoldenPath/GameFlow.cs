using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Content;
using RuntimeFlow.Contexts;
using RuntimeFlow.Initialization.Graph;
using RuntimeFlow.Pipeline;

namespace RuntimeFlow.Flow
{
    /// <summary>
    /// The opinionated golden path for a typical mobile game startup. Describes the flow in
    /// intent vocabulary (config → auth → profile → catalog → entry scene) and translates it
    /// onto the full RuntimeFlow machinery — scopes, stage markers, wave scheduling, health,
    /// progress — so new projects never need to see the advanced surface while it remains
    /// available through <see cref="GameFlowBuilder.Advanced"/>.
    ///
    /// Sources can be classes derived from <see cref="ContentSource{TData}"/> (data-flow edges
    /// declared by constructor parameters of type <c>IContentSource&lt;TOther&gt;</c>) or plain
    /// load delegates with explicit failure policies and <c>after:</c> tokens:
    ///
    /// <code>
    /// var game = await GameFlow.Create()
    ///     .Config("remote-config", LoadRemoteConfig,
    ///             ContentPolicy&lt;RemoteConfigSnapshot&gt;.Required())
    ///     .Auth("gpg", LoadGpgSignIn,
    ///           ContentPolicy&lt;AuthSnapshot&gt;.Optional(AuthSnapshot.Anonymous))
    ///     .Profile("server-profile", LoadServerProfile)
    ///     .Catalog&lt;CatalogSource, CatalogSnapshot&gt;()
    ///     .Entry&lt;PreloaderScene&gt;()
    ///     .StartAsync();
    /// </code>
    /// </summary>
    public static class GameFlow
    {
        public static GameFlowBuilder Create(Action<IGameContextBuilder>? advanced = null)
            => new GameFlowBuilder(advanced);
    }

    public sealed partial class GameFlowBuilder
    {
        private readonly Action<IGameContextBuilder>? _advanced;
        private readonly List<Action<IGameContextBuilder>> _steps = new();
        private readonly List<ContentPlanEntryBuilder> _plan = new();
        private Type? _entrySceneType;
        private Type? _entryResolverType;

        internal GameFlowBuilder(Action<IGameContextBuilder>? advanced)
        {
            _advanced = advanced;
        }

        private GameFlowBuilder Step(Action<IGameContextBuilder> step)
        {
            if (step == null) throw new ArgumentNullException(nameof(step));
            _steps.Add(step);
            return this;
        }

        // ---------- vocabulary: config ----------

        /// <summary>
        /// Contract: Global scope. Required unless policy says otherwise.
        /// Must not implement session stage markers — use Profile/Catalog for session content.
        /// </summary>
        public GameFlowBuilder Config<TSource, TData>(ContentPolicy<TData>? policy = null)
            where TSource : ContentSource<TData>
            where TData : class
        {
            ValidateNoSessionStageMarker<TSource>("Config");
            TrackPlan<TSource, TData>(GameContextType.Global, typeof(TSource).Name, policy);
            return Step(b => b.Global().Content<TSource, TData>());
        }

        public GameFlowBuilder Config<TData>(
            string sourceName,
            Func<FlowLoadContext, CancellationToken, Task<TData>> load,
            ContentPolicy<TData>? policy = null,
            IReadOnlyList<SourceToken<TData>>? after = null)
            where TData : class
            => ConfigDelegate(sourceName, load, policy, after);

        // ---------- vocabulary: auth ----------

        /// <summary>
        /// Contract: Session scope, Platform stage. The source must implement
        /// <c>IPlatformStartupInitializableService</c>; combine
        /// <c>IUserInteractionGatedInitializableService</c> when sign-in shows a dialog
        /// (health watchdog exempted).
        /// </summary>
        public GameFlowBuilder Auth<TSource, TData>(ContentPolicy<TData>? policy = null)
            where TSource : ContentSource<TData>
            where TData : class
        {
            TrackPlan<TSource, TData>(GameContextType.Session, typeof(TSource).Name, policy);
            return Step(b => b.Session().Content<TSource, TData>());
        }

        public GameFlowBuilder Auth<TData>(
            string sourceName,
            Func<FlowLoadContext, CancellationToken, Task<TData>> load,
            ContentPolicy<TData>? policy = null,
            IReadOnlyList<SourceToken<TData>>? after = null)
            where TData : class
            => AuthDelegate(sourceName, load, policy, after);

        // ---------- vocabulary: profile ----------

        /// <summary>
        /// Contract: Session scope. Chain after authentication by injecting
        /// <c>IContentSource&lt;TAuthData&gt;</c> (implicit edge) or passing an
        /// <c>after:</c> token.
        /// </summary>
        public GameFlowBuilder Profile<TSource, TData>(ContentPolicy<TData>? policy = null)
            where TSource : ContentSource<TData>
            where TData : class
        {
            ValidateNoPlatformStageMarker<TSource>("Profile");
            TrackPlan<TSource, TData>(GameContextType.Session, typeof(TSource).Name, policy);
            return Step(b => b.Session().Content<TSource, TData>());
        }

        public GameFlowBuilder Profile<TData>(
            string sourceName,
            Func<FlowLoadContext, CancellationToken, Task<TData>> load,
            ContentPolicy<TData>? policy = null,
            IReadOnlyList<SourceToken<TData>>? after = null)
            where TData : class
            => ProfileDelegate(sourceName, load, policy, after);

        // ---------- vocabulary: catalog ----------

        /// <summary>
        /// Contract: Session scope, Content stage. The source must implement
        /// <c>IContentStartupInitializableService</c>.
        /// </summary>
        public GameFlowBuilder Catalog<TSource, TData>(ContentPolicy<TData>? policy = null)
            where TSource : ContentSource<TData>
            where TData : class
        {
            TrackPlan<TSource, TData>(GameContextType.Session, typeof(TSource).Name, policy);
            return Step(b => b.Session().Content<TSource, TData>());
        }

        public GameFlowBuilder Catalog<TData>(
            string sourceName,
            Func<FlowLoadContext, CancellationToken, Task<TData>> load,
            ContentPolicy<TData>? policy = null,
            IReadOnlyList<SourceToken<TData>>? after = null)
            where TData : class
            => CatalogDelegate(sourceName, load, policy, after);

        // ---------- vocabulary: scenes / ui / services ----------

        /// <summary>Declares and registers a scene scope type.</summary>
        public GameFlowBuilder Scene<TScene>() where TScene : ISceneScope, new()
            => Step(b => b.Scene<TScene>());

        /// <summary>
        /// Declares a static entry scene: loaded automatically after boot. For dynamic routing
        /// (tutorial vs meta vs session rejoin), use <see cref="ResolveEntryWith{TResolver}"/> instead.
        /// </summary>
        public GameFlowBuilder Entry<TScene>() where TScene : ISceneScope, new()
        {
            if (_entrySceneType != null)
                throw new InvalidOperationException(
                    $"GameFlow.Entry<{typeof(TScene).Name}> conflicts with previously declared " +
                    $"entry scene '{_entrySceneType.Name}'. Only one entry scene is allowed.");
            _entrySceneType = typeof(TScene);
            _entryResolverType = null;
            return this;
        }

        /// <summary>
        /// Declares dynamic entry routing: after boot, the resolver determines which scene to load.
        /// Use this for flows where the destination depends on player state (tutorial for new players,
        /// meta for returning, session rejoin after disconnect).
        /// </summary>
        public GameFlowBuilder ResolveEntryWith<TResolver>()
            where TResolver : IEntryRouteResolver
        {
            _entrySceneType = null;
            _entryResolverType = typeof(TResolver);
            return this;
        }

        /// <summary>Loading-screen / first-UI service. Session scope, UI stage — the source must implement <c>IUiStartupInitializableService</c>.</summary>
        public GameFlowBuilder LoadingUi<TUiService>()
            where TUiService : class, IAsyncInitializableService
        {
            RequireStageMarker<TUiService, IUiStartupInitializableService>("LoadingUi");
            return Step(b => b.Session().Register<TUiService>(DiLifetime.Singleton));
        }

        /// <summary>Registers an additional session service (escape hatch without leaving the golden path).</summary>
        public GameFlowBuilder Service<TService>()
            where TService : class
            => Step(b => b.Session().Register<TService>(DiLifetime.Singleton));

        public GameFlowBuilder Service<TInterface, TImplementation>()
            where TImplementation : class, TInterface
            where TInterface : class
            => Step(b => b.Session().Register<TInterface, TImplementation>(DiLifetime.Singleton));

        /// <summary>
        /// Escape hatch onto the full builder API for anything the golden path does not name.
        /// Steps run in declaration order, after all vocabulary steps.
        /// </summary>
        public GameFlowBuilder Advanced(Action<IGameContextBuilder> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            _steps.Add(configure);
            return this;
        }

        // ---------- delegate plumbing ----------

        // ---------- validation ----------

        private static void RequireStageMarker<TSource, TMarker>(string vocabularyName)
            where TMarker : class
        {
            if (!typeof(TMarker).IsAssignableFrom(typeof(TSource)))
                throw new InvalidOperationException(
                    $"GameFlow.{vocabularyName}<{typeof(TSource).Name}> requires the source to implement " +
                    $"'{typeof(TMarker).FullName}' so it is scheduled in the intended startup stage. " +
                    "Combine the interface on the source class, or use Advanced(...) for custom placement.");
        }

        private static void ValidateNoSessionStageMarker<TSource>(string vocabularyName)
        {
            if (typeof(IStartupStageInitializableService).IsAssignableFrom(typeof(TSource)))
                throw new InvalidOperationException(
                    $"GameFlow.{vocabularyName}<{typeof(TSource).Name}> places the source in the global scope, " +
                    $"but it implements a session startup-stage marker ('{nameof(IStartupStageInitializableService)}'). " +
                    "Use Profile(...) / Catalog(...) instead, or remove the stage marker.");
        }

        private static void ValidateNoPlatformStageMarker<TSource>(string vocabularyName)
        {
            if (typeof(IPlatformStartupInitializableService).IsAssignableFrom(typeof(TSource)))
                throw new InvalidOperationException(
                    $"GameFlow.{vocabularyName}<{typeof(TSource).Name}> is regular session content, but it " +
                    $"implements the platform stage marker ('{nameof(IPlatformStartupInitializableService)}'). " +
                    "Use Auth(...) for platform sources.");
        }

        private GameFlowBuilder ConfigDelegate<TData>(string sourceName, Func<FlowLoadContext, CancellationToken, Task<TData>> load, ContentPolicy<TData>? policy, IReadOnlyList<SourceToken<TData>>? after)
            where TData : class
        {
            var primary = RegisterDelegate(GameContextType.Global, sourceName, load, policy, after);
            TrackPlanDelegate(GameContextType.Global, sourceName, primary, policy, after);
            return this;
        }

        private GameFlowBuilder AuthDelegate<TData>(string sourceName, Func<FlowLoadContext, CancellationToken, Task<TData>> load, ContentPolicy<TData>? policy, IReadOnlyList<SourceToken<TData>>? after)
            where TData : class
        {
            var primary = RegisterDelegate(GameContextType.Session, sourceName, load, policy, after);
            TrackPlanDelegate(GameContextType.Session, sourceName, primary, policy, after);
            return this;
        }

        private GameFlowBuilder ProfileDelegate<TData>(string sourceName, Func<FlowLoadContext, CancellationToken, Task<TData>> load, ContentPolicy<TData>? policy, IReadOnlyList<SourceToken<TData>>? after)
            where TData : class
        {
            var primary = RegisterDelegate(GameContextType.Session, sourceName, load, policy, after);
            TrackPlanDelegate(GameContextType.Session, sourceName, primary, policy, after);
            return this;
        }

        private GameFlowBuilder CatalogDelegate<TData>(string sourceName, Func<FlowLoadContext, CancellationToken, Task<TData>> load, ContentPolicy<TData>? policy, IReadOnlyList<SourceToken<TData>>? after)
            where TData : class
        {
            var primary = RegisterDelegate(GameContextType.Session, sourceName, load, policy, after);
            TrackPlanDelegate(GameContextType.Session, sourceName, primary, policy, after);
            return this;
        }

        private Type RegisterDelegate<TData>(
            GameContextType scope,
            string sourceName,
            Func<FlowLoadContext, CancellationToken, Task<TData>> load,
            ContentPolicy<TData>? policy,
            IReadOnlyList<SourceToken<TData>>? after)
            where TData : class
        {
            var primary = typeof(DelegateContentSource<TData>);
            var edgeTypes = after?
                .Select(t => t.EdgeType)
                .Distinct()
                .ToArray() ?? Array.Empty<Type>();

            ContentEdgeRegistry.Set(primary, edgeTypes);

            Step(b =>
            {
                var concrete = (GameContextBuilder)b;
                var source = new DelegateContentSource<TData>(sourceName, load);
                if (policy != null)
                    source.Policy(policy.IsOptional, policy.Fallback);
                concrete.RegisterInstanceDeferredForDiscovery(
                    scope,
                    source,
                    primary,
                    extraExposedTypes: new[] { typeof(IContentSource<TData>), typeof(IContentSourceInfo) },
                    onContextAvailable: context => source.AttachResolver(t => context.Resolve(t)));
            });
            return primary;
        }

        // ---------- plan tracking ----------

        private void TrackPlan<TSource, TData>(GameContextType scope, string name, ContentPolicy<TData>? policy)
            where TSource : ContentSource<TData>
            where TData : class
        {
            _plan.Add(new ContentPlanEntryBuilder(
                scope, name, typeof(TSource), typeof(TData),
                required: policy?.IsOptional != true,
                dependsOn: Array.Empty<string>(),
                isDelegate: false));
        }

        private void TrackPlanDelegate<TData>(GameContextType scope, string name, Type primary, ContentPolicy<TData>? policy, IReadOnlyList<SourceToken<TData>>? after)
            where TData : class
        {
            _plan.Add(new ContentPlanEntryBuilder(
                scope, name, primary, typeof(TData),
                required: policy?.IsOptional != true,
                dependsOn: after?.Select(t => t.SourceName).ToArray() ?? Array.Empty<string>(),
                isDelegate: true));
        }

        // ---------- plan inspection ----------

        /// <summary>
        /// Static startup plan: nodes, dependency edges with origins, topological order per
        /// scope. Available before StartAsync; throws when the plan contains unresolvable
        /// content dependencies so misconfiguration surfaces at composition time.
        /// </summary>
        public IReadOnlyList<ContentPlanEntry> DescribeStartupPlan()
        {
            var dataToName = new Dictionary<Type, string>();
            foreach (var p in _plan)
                dataToName[p.DataType] = p.Name;

            // Merge implicit constructor data-flow edges into the declared ones.
            var merged = new List<ContentPlanEntry>();
            foreach (var p in _plan)
            {
                var dependsOn = new List<string>(p.DependsOn);
                if (!p.IsDelegate && p.ImplType != null)
                {
                    foreach (var dep in InitializationGraphRules.ResolveConstructorDependencies(p.ImplType))
                    {
                        if (!InitializationGraphRules.IsClosedContentSourceType(dep))
                            continue;
                        var dataType = dep.GetGenericArguments()[0];
                        if (!dataToName.TryGetValue(dataType, out var sourceName))
                            throw new InvalidOperationException(
                                $"Startup plan error: '{p.Name}' depends on content source producing " +
                                $"'{dataType.Name}', but no such source is registered in this flow. " +
                                "Add it via Config/Auth/Profile/Catalog.");
                        if (sourceName != p.Name && !dependsOn.Contains(sourceName))
                            dependsOn.Add(sourceName);
                    }
                }
                merged.Add(new ContentPlanEntry(p.Scope, p.Name, p.IsDelegate ? null : p.ImplType, p.Required, dependsOn));
            }

            var entries = merged.ToArray();

            foreach (var e in entries)
                foreach (var depName in e.DependsOn)
                    if (entries.All(x => x.SourceName != depName))
                        throw new InvalidOperationException(
                            $"Startup plan error: '{e.SourceName}' declares After('{depName}'), but no source with that name is registered.");

            // Topological order within each scope (Kahn, deterministic by declaration order).
            var result = new List<ContentPlanEntry>();
            foreach (var scopeGroup in entries.GroupBy(e => e.Scope).OrderBy(g => g.Key))
            {
                var pending = new Queue<ContentPlanEntry>(scopeGroup);
                var placedNames = new HashSet<string>();
                var guard = pending.Count + 1;
                while (pending.Count > 0 && guard-- > 0)
                {
                    var progressed = false;
                    var count = pending.Count;
                    for (var i = 0; i < count; i++)
                    {
                        var candidate = pending.Dequeue();
                        var satisfied = candidate.DependsOn.All(dep =>
                            !entries.Any(x => x.SourceName == dep && x.Scope == scopeGroup.Key)
                            || placedNames.Contains(dep));
                        if (satisfied)
                        {
                            result.Add(candidate);
                            placedNames.Add(candidate.SourceName);
                            progressed = true;
                        }
                        else
                        {
                            pending.Enqueue(candidate);
                        }
                    }
                    if (!progressed)
                        throw new InvalidOperationException(
                            $"Startup plan contains a content dependency cycle in scope {scopeGroup.Key}: " +
                            string.Join(" -> ", pending.Select(p => p.SourceName)));
                }
            }

            return result;
        }

        public string DescribeStartupPlanText()
        {
            var lines = new List<string>();
            foreach (var e in DescribeStartupPlan())
            {
                var deps = e.DependsOn.Count > 0 ? "  after: " + string.Join(", ", e.DependsOn) : string.Empty;
                lines.Add($"[{e.Scope}] {e.SourceName} ({(e.Required ? "required" : "optional")}){deps}");
            }
            return string.Join(Environment.NewLine, lines);
        }

        // ---------- execution ----------

        private void BuildCore(IGameContextBuilder builder)
        {
            builder.DefineGlobalScope();
            builder.DefineSessionScope();
            foreach (var step in _steps)
                step(builder);
            _advanced?.Invoke(builder);
        }

        /// <summary>
        /// Forces the fully deterministic inline scheduler for this flow. Intended for tests:
        /// immune to ambient main-thread captures that leak between tests through statics.
        /// </summary>
        public GameFlowBuilder DeterministicScheduler()
        {
            _deterministicScheduler = true;
            return this;
        }

        private bool _deterministicScheduler;

        /// <summary>
        /// Hard deadline for the whole boot (global + session + entry scene). On expiry the
        /// pipeline is disposed and a TimeoutException carrying the last runtime status is
        /// thrown. Default: 5 minutes.
        /// </summary>
        public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Builds and initializes the game (global + session). When an entry scene was
        /// declared via <see cref="Entry{TScene}"/>, it loads automatically after boot.
        /// </summary>
        public async Task<GameHandle> StartAsync(CancellationToken cancellationToken = default)
        {
            var pipeline = RuntimePipeline.Create(BuildCore, options =>
            {
                if (_deterministicScheduler)
                    options.ExecutionScheduler = InlineStrictInitializationExecutionScheduler.Instance;
            });
            try
            {
                var bootTask = BootAsync(pipeline, cancellationToken);
                var completed = await Task.WhenAny(bootTask, Task.Delay(StartupTimeout, cancellationToken)).ConfigureAwait(false);
                if (completed != bootTask)
                {
                    var status = pipeline.GetRuntimeStatus();
                    throw new TimeoutException(
                        $"GameFlow startup exceeded {StartupTimeout}. Last status: [{status.State}] " +
                        $"{status.CurrentOperationCode}: {status.Message}");
                }
                await bootTask.ConfigureAwait(false);
            }
            catch
            {
                await pipeline.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            return new GameHandle(pipeline);
        }

        private async Task BootAsync(RuntimePipeline pipeline, CancellationToken ct)
        {
            await pipeline.InitializeAsync(cancellationToken: ct).ConfigureAwait(false);

            if (_entryResolverType != null)
            {
                var resolver = (IEntryRouteResolver)Activator.CreateInstance(_entryResolverType)!;
                var route = await resolver.ResolveAsync(ct).ConfigureAwait(false);
                await pipeline.LoadSceneAsync(route.SceneType, cancellationToken: ct).ConfigureAwait(false);
            }
            else if (_entrySceneType != null)
            {
                await pipeline.LoadSceneAsync(_entrySceneType, cancellationToken: ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>A started game: the live pipeline plus typed accessors for consumers.</summary>
    public sealed class GameHandle : IAsyncDisposable
    {
        public GameHandle(RuntimePipeline pipeline)
        {
            Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        }

        public RuntimePipeline Pipeline { get; }

        public IGameContext SessionContext => Pipeline.SessionContext;

        /// <summary>The loaded gameplay scene context, available only after a scene has been loaded.</summary>
        public IGameContext? SceneContext => Pipeline.Builder.SceneContext;

        /// <summary>Resolves a service from the session context.</summary>
        public T Get<T>() where T : class => SessionContext.Resolve<T>();

        public Task LoadSceneAsync<TScene>(CancellationToken cancellationToken = default)
            => Pipeline.LoadSceneAsync<TScene>(cancellationToken: cancellationToken);

        public Task RestartAsync(CancellationToken cancellationToken = default)
            => Pipeline.RestartSessionAsync(cancellationToken: cancellationToken);

        public ValueTask DisposeAsync() => Pipeline.DisposeAsync();
    }

    /// <summary>A single content node of the static startup plan.</summary>
    public sealed class ContentPlanEntry
    {
        public ContentPlanEntry(
            GameContextType scope, string sourceName, Type? implementationType,
            bool required, IReadOnlyList<string> dependsOn)
        {
            Scope = scope; SourceName = sourceName; ImplementationType = implementationType;
            Required = required; DependsOn = dependsOn;
        }

        public GameContextType Scope { get; }
        public string SourceName { get; }
        public Type? ImplementationType { get; }
        public bool Required { get; }
        public IReadOnlyList<string> DependsOn { get; }
    }

    internal sealed class ContentPlanEntryBuilder
    {
        public ContentPlanEntryBuilder(
            GameContextType scope, string name, Type? implType, Type dataType,
            bool required, IReadOnlyList<string> dependsOn, bool isDelegate)
        {
            Scope = scope; Name = name; ImplType = implType; DataType = dataType;
            Required = required; DependsOn = dependsOn; IsDelegate = isDelegate;
        }

        public GameContextType Scope { get; }
        public string Name { get; }
        public Type? ImplType { get; }
        public Type DataType { get; }
        public bool Required { get; }
        public IReadOnlyList<string> DependsOn { get; }
        public bool IsDelegate { get; }

        public ContentPlanEntry Build()
            => new(Scope, Name, IsDelegate ? null : ImplType, Required, DependsOn);
    }
}
