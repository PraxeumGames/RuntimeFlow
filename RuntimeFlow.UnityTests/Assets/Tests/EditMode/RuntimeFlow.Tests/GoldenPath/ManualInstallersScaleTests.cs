using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Content;
using RuntimeFlow.Contexts;
using RuntimeFlow.Flow;
using RuntimeFlow.Testing;

namespace RuntimeFlow.Tests
{
    /// <summary>
    /// Scale proof for manual mass registration — the way real projects work: feature code
    /// registers its services programmatically in loops (factories, arguments, closures stay
    /// possible), and every service that constructor-injects a content source automatically
    /// gets a data-flow edge onto it. No attributes, no scanning.
    ///
    /// 1 global content source + 24 session services registered in one loop:
    /// even services consume the config through constructor injection (implicit edge onto the
    /// global source), odd services are plain. Data correctness asserted across the whole set.
    /// </summary>
    public sealed class ManualInstallersScaleTests
    {
        public sealed class ScaleConfig { public string Environment { get; set; } = "dev"; }

        private sealed class ScaleConfigSource : ContentSource<ScaleConfig>, IGlobalInitializableService
        {
            public static int LoadCount;
            public override string SourceName => "scale-config";
            protected override Task<ScaleConfig> LoadAsync(CancellationToken ct)
            {
                Interlocked.Increment(ref LoadCount);
                return Task.FromResult(new ScaleConfig { Environment = "prod" });
            }
        }

        private const int ServiceCount = 24;

        private static readonly string[] Initialized = new string[ServiceCount];
        private static readonly string[] ObservedEnvironment = new string[ServiceCount];

        // Svc02..Svc24 (even ids, 1-based) consume the config; odd ones are plain services.
        private sealed class Svc01 : ISessionInitializableService
        {
            public Task InitializeAsync(CancellationToken ct) { Initialized[0] = nameof(Svc01); return Task.CompletedTask; }
        }
        private sealed class Svc02 : ISessionInitializableService
        {
            private readonly IContentSource<ScaleConfig> _config;
            public Svc02(IContentSource<ScaleConfig> config) => _config = config;
            public async Task InitializeAsync(CancellationToken ct)
            {
                await Task.Yield();
                ObservedEnvironment[1] = _config.Data.Environment;
                Initialized[1] = nameof(Svc02);
            }
        }
        private sealed class Svc03 : ISessionInitializableService
        {
            public Task InitializeAsync(CancellationToken ct) { Initialized[2] = nameof(Svc03); return Task.CompletedTask; }
        }
        private sealed class Svc04 : ISessionInitializableService
        {
            private readonly IContentSource<ScaleConfig> _config;
            public Svc04(IContentSource<ScaleConfig> config) => _config = config;
            public async Task InitializeAsync(CancellationToken ct)
            {
                await Task.Yield();
                ObservedEnvironment[3] = _config.Data.Environment;
                Initialized[3] = nameof(Svc04);
            }
        }
        private sealed class Svc05 : ISessionInitializableService
        {
            public Task InitializeAsync(CancellationToken ct) { Initialized[4] = nameof(Svc05); return Task.CompletedTask; }
        }
        private sealed class Svc06 : ISessionInitializableService
        {
            private readonly IContentSource<ScaleConfig> _config;
            public Svc06(IContentSource<ScaleConfig> config) => _config = config;
            public async Task InitializeAsync(CancellationToken ct)
            {
                await Task.Yield();
                ObservedEnvironment[5] = _config.Data.Environment;
                Initialized[5] = nameof(Svc06);
            }
        }
        private sealed class Svc07 : ISessionInitializableService
        {
            public Task InitializeAsync(CancellationToken ct) { Initialized[6] = nameof(Svc07); return Task.CompletedTask; }
        }
        private sealed class Svc08 : ISessionInitializableService
        {
            private readonly IContentSource<ScaleConfig> _config;
            public Svc08(IContentSource<ScaleConfig> config) => _config = config;
            public async Task InitializeAsync(CancellationToken ct)
            {
                await Task.Yield();
                ObservedEnvironment[7] = _config.Data.Environment;
                Initialized[7] = nameof(Svc08);
            }
        }
        private sealed class Svc09 : ISessionInitializableService
        {
            public Task InitializeAsync(CancellationToken ct) { Initialized[8] = nameof(Svc09); return Task.CompletedTask; }
        }
        private sealed class Svc10 : ISessionInitializableService
        {
            private readonly IContentSource<ScaleConfig> _config;
            public Svc10(IContentSource<ScaleConfig> config) => _config = config;
            public async Task InitializeAsync(CancellationToken ct)
            {
                await Task.Yield();
                ObservedEnvironment[9] = _config.Data.Environment;
                Initialized[9] = nameof(Svc10);
            }
        }
        private sealed class Svc11 : ISessionInitializableService
        {
            public Task InitializeAsync(CancellationToken ct) { Initialized[10] = nameof(Svc11); return Task.CompletedTask; }
        }
        private sealed class Svc12 : ISessionInitializableService
        {
            private readonly IContentSource<ScaleConfig> _config;
            public Svc12(IContentSource<ScaleConfig> config) => _config = config;
            public async Task InitializeAsync(CancellationToken ct)
            {
                await Task.Yield();
                ObservedEnvironment[11] = _config.Data.Environment;
                Initialized[11] = nameof(Svc12);
            }
        }
        private sealed class Svc13 : ISessionInitializableService
        {
            public Task InitializeAsync(CancellationToken ct) { Initialized[12] = nameof(Svc13); return Task.CompletedTask; }
        }
        private sealed class Svc14 : ISessionInitializableService
        {
            private readonly IContentSource<ScaleConfig> _config;
            public Svc14(IContentSource<ScaleConfig> config) => _config = config;
            public async Task InitializeAsync(CancellationToken ct)
            {
                await Task.Yield();
                ObservedEnvironment[13] = _config.Data.Environment;
                Initialized[13] = nameof(Svc14);
            }
        }
        private sealed class Svc15 : ISessionInitializableService
        {
            public Task InitializeAsync(CancellationToken ct) { Initialized[14] = nameof(Svc15); return Task.CompletedTask; }
        }
        private sealed class Svc16 : ISessionInitializableService
        {
            private readonly IContentSource<ScaleConfig> _config;
            public Svc16(IContentSource<ScaleConfig> config) => _config = config;
            public async Task InitializeAsync(CancellationToken ct)
            {
                await Task.Yield();
                ObservedEnvironment[15] = _config.Data.Environment;
                Initialized[15] = nameof(Svc16);
            }
        }
        private sealed class Svc17 : ISessionInitializableService
        {
            public Task InitializeAsync(CancellationToken ct) { Initialized[16] = nameof(Svc17); return Task.CompletedTask; }
        }
        private sealed class Svc18 : ISessionInitializableService
        {
            private readonly IContentSource<ScaleConfig> _config;
            public Svc18(IContentSource<ScaleConfig> config) => _config = config;
            public async Task InitializeAsync(CancellationToken ct)
            {
                await Task.Yield();
                ObservedEnvironment[17] = _config.Data.Environment;
                Initialized[17] = nameof(Svc18);
            }
        }
        private sealed class Svc19 : ISessionInitializableService
        {
            public Task InitializeAsync(CancellationToken ct) { Initialized[18] = nameof(Svc19); return Task.CompletedTask; }
        }
        private sealed class Svc20 : ISessionInitializableService
        {
            private readonly IContentSource<ScaleConfig> _config;
            public Svc20(IContentSource<ScaleConfig> config) => _config = config;
            public async Task InitializeAsync(CancellationToken ct)
            {
                await Task.Yield();
                ObservedEnvironment[19] = _config.Data.Environment;
                Initialized[19] = nameof(Svc20);
            }
        }
        private sealed class Svc21 : ISessionInitializableService
        {
            public Task InitializeAsync(CancellationToken ct) { Initialized[20] = nameof(Svc21); return Task.CompletedTask; }
        }
        private sealed class Svc22 : ISessionInitializableService
        {
            private readonly IContentSource<ScaleConfig> _config;
            public Svc22(IContentSource<ScaleConfig> config) => _config = config;
            public async Task InitializeAsync(CancellationToken ct)
            {
                await Task.Yield();
                ObservedEnvironment[21] = _config.Data.Environment;
                Initialized[21] = nameof(Svc22);
            }
        }
        private sealed class Svc23 : ISessionInitializableService
        {
            public Task InitializeAsync(CancellationToken ct) { Initialized[22] = nameof(Svc23); return Task.CompletedTask; }
        }
        private sealed class Svc24 : ISessionInitializableService
        {
            private readonly IContentSource<ScaleConfig> _config;
            public Svc24(IContentSource<ScaleConfig> config) => _config = config;
            public async Task InitializeAsync(CancellationToken ct)
            {
                await Task.Yield();
                ObservedEnvironment[23] = _config.Data.Environment;
                Initialized[23] = nameof(Svc24);
            }
        }

        private static readonly Type[] ServiceTypes =
        {
            typeof(Svc01), typeof(Svc02), typeof(Svc03), typeof(Svc04),
            typeof(Svc05), typeof(Svc06), typeof(Svc07), typeof(Svc08),
            typeof(Svc09), typeof(Svc10), typeof(Svc11), typeof(Svc12),
            typeof(Svc13), typeof(Svc14), typeof(Svc15), typeof(Svc16),
            typeof(Svc17), typeof(Svc18), typeof(Svc19), typeof(Svc20),
            typeof(Svc21), typeof(Svc22), typeof(Svc23), typeof(Svc24),
        };

        [Test]
        public async Task FullScale_AllTwentyFourInitialize_ConfigDataCorrectThroughEdges()
        {
            Array.Clear(Initialized, 0, Initialized.Length);
            Array.Clear(ObservedEnvironment, 0, ObservedEnvironment.Length);

            await using var game = await BuildScaleFlow().StartAsync();

            for (var i = 0; i < ServiceCount; i++)
                Assert.IsNotNull(Initialized[i], $"Svc{i + 1:D2} must have initialized.");

            for (var i = 1; i < ServiceCount; i += 2)
                Assert.AreEqual("prod", ObservedEnvironment[i],
                    $"{nameof(Svc02)[0..3]}{i + 1:D2} must observe loaded config via its edge.");

            Assert.AreEqual(1, ScaleConfigSource.LoadCount,
                "Global content must load exactly once regardless of consumer count.");
        }

        private static TestPipelineBuilder BuildScaleFlow()
            => TestPipeline.Create(cfg =>
                    cfg.Global().Content<ScaleConfigSource, ScaleConfig>())
                .Configure(b =>
                {
                    var concrete = (GameContextBuilder)b;
                    var session = concrete.CreateScopeRegistrationBuilder(typeof(SessionScope));
                    foreach (var type in ServiceTypes)
                        session.Register(type, DiLifetime.Singleton);
                });
    }
}
