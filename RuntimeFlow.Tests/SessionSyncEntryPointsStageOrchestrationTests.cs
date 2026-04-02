using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using VContainer;
using VContainer.Unity;
using Xunit;

namespace RuntimeFlow.Tests;

public sealed class SessionSyncEntryPointsStageOrchestrationTests
{
    [Fact]
    public async Task SessionSyncEntryPoints_ExecutesStagedInitializablesInCanonicalOrder_ThenNonStagedFallback()
    {
        var (rootResolver, scopeResolver) = CreateResolver(
            registerServices: builder =>
            {
                builder.Register<ExecutionRecorder>(Lifetime.Singleton).As<IExecutionRecorder>();
                builder.Register<PreBootstrapStageInitializable>(Lifetime.Singleton)
                    .As<IPreBootstrapTag>()
                    .As<IInitializable>()
                    .As<IPreBootstrapStartupInitializableService>();
                builder.Register<PlatformStageInitializable>(Lifetime.Singleton)
                    .As<IPlatformTag>()
                    .As<IInitializable>()
                    .As<IPlatformStartupInitializableService>();
                builder.Register<ContentStageInitializable>(Lifetime.Singleton)
                    .As<IContentTag>()
                    .As<IInitializable>()
                    .As<IContentStartupInitializableService>();
                builder.Register<SessionStageInitializable>(Lifetime.Singleton)
                    .As<ISessionTag>()
                    .As<IInitializable>()
                    .As<ISessionStartupInitializableService>();
                builder.Register<UiStageInitializable>(Lifetime.Singleton)
                    .As<IUiTag>()
                    .As<IInitializable>()
                    .As<IUiStartupInitializableService>();
                builder.Register<NonStagedInitializable>(Lifetime.Singleton)
                    .As<INonStagedTag>()
                    .As<IInitializable>();
            },
            getInitializableRegistrations: root => new[]
            {
                GetRegistration<IUiTag>(root),
                GetRegistration<INonStagedTag>(root),
                GetRegistration<ISessionTag>(root),
                GetRegistration<IContentTag>(root),
                GetRegistration<IPreBootstrapTag>(root),
                GetRegistration<IPlatformTag>(root)
            });

        try
        {
            var service = new RuntimeFlowSessionSyncEntryPointsInitializationService(
                scopeResolver,
                CreateUnmanagedEntryPointSettings());
            await service.InitializeAsync(CancellationToken.None);

            var recorder = scopeResolver.Resolve<IExecutionRecorder>();
            Assert.Equal(
                new[] { "prebootstrap", "platform", "content", "session", "ui", "nonstaged" },
                recorder.Events);
        }
        finally
        {
            scopeResolver.Dispose();
            rootResolver.Dispose();
        }
    }

    [Fact]
    public async Task SessionSyncEntryPoints_DeduplicatesByImplementationType_InSinglePass()
    {
        var (rootResolver, scopeResolver) = CreateResolver(
            registerServices: builder =>
            {
                builder.Register<ExecutionRecorder>(Lifetime.Singleton).As<IExecutionRecorder>();
                builder.Register<DuplicateSessionStageInitializable>(Lifetime.Transient)
                    .As<IDuplicateTagA>()
                    .As<IInitializable>()
                    .As<ISessionStartupInitializableService>();
                builder.Register<DuplicateSessionStageInitializable>(Lifetime.Transient)
                    .As<IDuplicateTagB>()
                    .As<IInitializable>()
                    .As<ISessionStartupInitializableService>();
            },
            getInitializableRegistrations: root => new[]
            {
                GetRegistration<IDuplicateTagA>(root),
                GetRegistration<IDuplicateTagB>(root)
            });

        try
        {
            var service = new RuntimeFlowSessionSyncEntryPointsInitializationService(
                scopeResolver,
                CreateUnmanagedEntryPointSettings());
            await service.InitializeAsync(CancellationToken.None);

            var recorder = scopeResolver.Resolve<IExecutionRecorder>();
            Assert.Equal(new[] { "duplicate" }, recorder.Events);
        }
        finally
        {
            scopeResolver.Dispose();
            rootResolver.Dispose();
        }
    }

    private static RuntimeFlowVContainerEntryPointsSettings CreateUnmanagedEntryPointSettings()
    {
        return new RuntimeFlowVContainerEntryPointsSettings(
            Array.Empty<Type>(),
            Array.Empty<Type>(),
            Array.Empty<Type>(),
            null);
    }

    private static (IObjectResolver Root, IObjectResolver Scope) CreateResolver(
        Action<IContainerBuilder> registerServices,
        Func<IObjectResolver, IReadOnlyList<Registration>> getInitializableRegistrations)
    {
        if (registerServices == null) throw new ArgumentNullException(nameof(registerServices));
        if (getInitializableRegistrations == null) throw new ArgumentNullException(nameof(getInitializableRegistrations));

        var rootBuilder = new ContainerBuilder();
        registerServices(rootBuilder);
        var rootResolver = rootBuilder.Build();
        var initializableRegistrations = getInitializableRegistrations(rootResolver);

        var scopeResolver = rootResolver.CreateScope(scopeBuilder =>
        {
            scopeBuilder.Register(new EntryPointRegistrationListBuilder<IInitializable>(initializableRegistrations));
            scopeBuilder.Register(new EntryPointRegistrationListBuilder<IStartable>(Array.Empty<Registration>()));
        });

        return (rootResolver, scopeResolver);
    }

    private static Registration GetRegistration<TService>(IObjectResolver resolver)
    {
        if (resolver == null) throw new ArgumentNullException(nameof(resolver));

        if (!resolver.TryGetRegistration(typeof(TService), out var registration) || registration == null)
        {
            throw new InvalidOperationException(
                $"Registration for '{typeof(TService).FullName}' is not available.");
        }

        return registration;
    }

    private sealed class EntryPointRegistrationListBuilder<TEntryPoint> : RegistrationBuilder
        where TEntryPoint : class
    {
        private readonly IReadOnlyList<Registration> _registrations;

        public EntryPointRegistrationListBuilder(IReadOnlyList<Registration> registrations)
            : base(typeof(IReadOnlyList<TEntryPoint>), Lifetime.Singleton)
        {
            _registrations = registrations ?? throw new ArgumentNullException(nameof(registrations));
            As(typeof(IReadOnlyList<TEntryPoint>));
        }

        public override Registration Build()
        {
            return new Registration(
                typeof(IReadOnlyList<TEntryPoint>),
                Lifetime.Singleton,
                new[] { typeof(IReadOnlyList<TEntryPoint>) },
                new EntryPointRegistrationListProvider(_registrations));
        }
    }

    private sealed class EntryPointRegistrationListProvider : IInstanceProvider, IEnumerable
    {
        private readonly IReadOnlyList<Registration> _registrations;

        public EntryPointRegistrationListProvider(IReadOnlyList<Registration> registrations)
        {
            _registrations = registrations ?? throw new ArgumentNullException(nameof(registrations));
        }

        public object SpawnInstance(IObjectResolver resolver)
        {
            return _registrations;
        }

        public IEnumerator GetEnumerator()
        {
            return _registrations.GetEnumerator();
        }
    }

    private interface IExecutionRecorder
    {
        IReadOnlyList<string> Events { get; }

        void Record(string value);
    }

    private sealed class ExecutionRecorder : IExecutionRecorder
    {
        private readonly List<string> _events = new();

        public IReadOnlyList<string> Events => _events;

        public void Record(string value)
        {
            _events.Add(value);
        }
    }

    private interface IPreBootstrapTag { }
    private interface IPlatformTag { }
    private interface IContentTag { }
    private interface ISessionTag { }
    private interface IUiTag { }
    private interface INonStagedTag { }
    private interface IDuplicateTagA { }
    private interface IDuplicateTagB { }

    private sealed class PreBootstrapStageInitializable :
        IInitializable,
        IPreBootstrapStartupInitializableService,
        IPreBootstrapTag
    {
        private readonly IExecutionRecorder _recorder;

        public PreBootstrapStageInitializable(IExecutionRecorder recorder) => _recorder = recorder;

        public void Initialize() => _recorder.Record("prebootstrap");

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class PlatformStageInitializable :
        IInitializable,
        IPlatformStartupInitializableService,
        IPlatformTag
    {
        private readonly IExecutionRecorder _recorder;

        public PlatformStageInitializable(IExecutionRecorder recorder) => _recorder = recorder;

        public void Initialize() => _recorder.Record("platform");

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ContentStageInitializable :
        IInitializable,
        IContentStartupInitializableService,
        IContentTag
    {
        private readonly IExecutionRecorder _recorder;

        public ContentStageInitializable(IExecutionRecorder recorder) => _recorder = recorder;

        public void Initialize() => _recorder.Record("content");

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SessionStageInitializable :
        IInitializable,
        ISessionStartupInitializableService,
        ISessionTag
    {
        private readonly IExecutionRecorder _recorder;

        public SessionStageInitializable(IExecutionRecorder recorder) => _recorder = recorder;

        public void Initialize() => _recorder.Record("session");

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class UiStageInitializable :
        IInitializable,
        IUiStartupInitializableService,
        IUiTag
    {
        private readonly IExecutionRecorder _recorder;

        public UiStageInitializable(IExecutionRecorder recorder) => _recorder = recorder;

        public void Initialize() => _recorder.Record("ui");

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NonStagedInitializable : IInitializable, INonStagedTag
    {
        private readonly IExecutionRecorder _recorder;

        public NonStagedInitializable(IExecutionRecorder recorder) => _recorder = recorder;

        public void Initialize() => _recorder.Record("nonstaged");
    }

    private sealed class DuplicateSessionStageInitializable :
        IInitializable,
        ISessionStartupInitializableService,
        IDuplicateTagA,
        IDuplicateTagB
    {
        private readonly IExecutionRecorder _recorder;

        public DuplicateSessionStageInitializable(IExecutionRecorder recorder) => _recorder = recorder;

        public void Initialize() => _recorder.Record("duplicate");

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
