using System;
using System.Collections.Generic;
using VContainer;

namespace RuntimeFlow.Demo
{
    /// <summary>
    /// The whole wiring of the demo game: two plain VContainer installers and the phase list. This is the
    /// only file a reader has to copy — the bootstrapper and the tests both go through it, so what the
    /// dashboard shows is exactly what a consumer would get.
    /// <code>
    /// var host = DemoGame.CreateHost(new FakeBackend(), new ChaosToggles());
    /// var result = await host.StartAsync();
    /// if (result.Outcome == StartupOutcome.Halted) ShowMaintenanceScreen(result.HaltReason);
    /// </code>
    /// </summary>
    public static class DemoGame
    {
        /// <summary>
        /// The ordered phase barriers: platform consent and gates first, then content downloads, then
        /// the warmup that content enables. A service without <c>[Init(Phase)]</c> lands in the last one.
        /// </summary>
        public static IReadOnlyList<string> Phases { get; } =
            Array.AsReadOnly(new[] { "platform", "content", "warmup" });

        /// <summary>
        /// Registers everything that survives a restart: the backend, the toggles and the two services
        /// whose results stay warm across sessions.
        /// </summary>
        public static void ConfigureGlobal(IContainerBuilder builder, FakeBackend backend, ChaosToggles chaos)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (backend == null) throw new ArgumentNullException(nameof(backend));
            if (chaos == null) throw new ArgumentNullException(nameof(chaos));

            builder.RegisterInstance(backend);
            builder.RegisterInstance(chaos);

            builder.RegisterInitializable<RemoteConfigService>();
            builder.RegisterInitializable<PlatformAuthService>();
        }

        /// <summary>
        /// Registers everything a restart rebuilds from scratch. The services see the global ones through
        /// their constructors, which is what orders them across the two scopes. The backend and the
        /// toggles are <em>not</em> registered here: the session is a child of the global scope and
        /// resolves both from it, so a restart cannot hand the session a second copy of either.
        /// </summary>
        public static void ConfigureSession(IContainerBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            builder.RegisterInitializable<GdprConsentService>();
            builder.RegisterInitializable<MaintenanceGateService>();
            builder.RegisterInitializable<PlayerProfileService>();
            builder.RegisterInitializable<CatalogService>();
            builder.RegisterInitializable<QuestWarmupService>();
        }

        /// <summary>
        /// Builds a host around the two installers with <see cref="Phases"/> applied.
        /// </summary>
        /// <param name="backend">The backend instance both scopes share; registered in the global scope.</param>
        /// <param name="chaos">The toggles both scopes share; registered in the global scope.</param>
        /// <param name="options">
        /// Options to start from; a fresh set is used when null. They are copied, so the caller's
        /// instance is never mutated — only the copy gets <see cref="Phases"/> applied.
        /// </param>
        public static RuntimeFlowHost CreateHost(
            FakeBackend backend,
            ChaosToggles chaos,
            RuntimeFlowOptions? options = null)
        {
            var effective = Copy(options);
            effective.Phases = Phases;
            return new RuntimeFlowHost(
                builder => ConfigureGlobal(builder, backend, chaos),
                ConfigureSession,
                effective);
        }

        /// <summary>
        /// Copies every knob of <paramref name="options"/> into a fresh instance, so applying the demo's
        /// phases cannot leak back into the caller's object (tests reuse one options instance).
        /// </summary>
        /// <param name="options">The options to copy, or null for the defaults.</param>
        private static RuntimeFlowOptions Copy(RuntimeFlowOptions? options)
        {
            var copy = new RuntimeFlowOptions();
            if (options == null) return copy;

            copy.Logger = options.Logger;
            copy.Phases = options.Phases;
            copy.DefaultPhase = options.DefaultPhase;
            copy.StallWarningAfter = options.StallWarningAfter;
            copy.CancellationGrace = options.CancellationGrace;
            copy.TimeoutMultiplier = options.TimeoutMultiplier;
            copy.MaxRestartsPerWindow = options.MaxRestartsPerWindow;
            copy.RestartWindow = options.RestartWindow;
            copy.Observers.AddRange(options.Observers);
            return copy;
        }
    }
}
