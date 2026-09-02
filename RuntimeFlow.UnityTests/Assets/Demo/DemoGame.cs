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
        /// their constructors, which is what orders them across the two scopes.
        /// </summary>
        public static void ConfigureSession(IContainerBuilder builder, FakeBackend backend, ChaosToggles chaos)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (backend == null) throw new ArgumentNullException(nameof(backend));
            if (chaos == null) throw new ArgumentNullException(nameof(chaos));

            builder.RegisterInstance(backend);
            builder.RegisterInstance(chaos);

            builder.RegisterInitializable<GdprConsentService>();
            builder.RegisterInitializable<MaintenanceGateService>();
            builder.RegisterInitializable<PlayerProfileService>();
            builder.RegisterInitializable<CatalogService>();
            builder.RegisterInitializable<QuestWarmupService>();
        }

        /// <summary>
        /// Builds a host around the two installers with <see cref="Phases"/> applied.
        /// </summary>
        /// <param name="backend">The backend instance both scopes share.</param>
        /// <param name="chaos">The toggles both scopes share.</param>
        /// <param name="options">Options to start from; a fresh set is used when null. Phases are overwritten.</param>
        public static RuntimeFlowHost CreateHost(
            FakeBackend backend,
            ChaosToggles chaos,
            RuntimeFlowOptions? options = null)
        {
            var effective = options ?? new RuntimeFlowOptions();
            effective.Phases = Phases;
            return new RuntimeFlowHost(
                builder => ConfigureGlobal(builder, backend, chaos),
                builder => ConfigureSession(builder, backend, chaos),
                effective);
        }
    }
}
