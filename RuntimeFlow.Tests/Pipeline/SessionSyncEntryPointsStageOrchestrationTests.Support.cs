using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Tests;

public sealed partial class SessionSyncEntryPointsStageOrchestrationTests
{
    private static RuntimeFlowVContainerEntryPointsSettings CreateUnmanagedEntryPointSettings()
    {
        return new RuntimeFlowVContainerEntryPointsSettings(
            Array.Empty<Type>(),
            Array.Empty<Type>(),
            Array.Empty<Type>(),
            null);
    }

    private static RuntimeFlowVContainerEntryPointsSettings CreateManagedSessionMarkerEntryPointSettings()
    {
        return new RuntimeFlowVContainerEntryPointsSettings(
            new[] { typeof(ISessionInitializableService) },
            Array.Empty<Type>(),
            Array.Empty<Type>(),
            null);
    }

    private static async Task<IReadOnlyList<string>> InitializeAndGetRecordedEventsAsync(
        (IObjectResolver Root, IObjectResolver Scope) resolvers,
        RuntimeFlowVContainerEntryPointsSettings settings)
    {
        var (rootResolver, scopeResolver) = resolvers;
        try
        {
            var service = new RuntimeFlowSessionSyncEntryPointsInitializationService(scopeResolver, settings);
            await service.InitializeAsync(CancellationToken.None);
            var recorder = scopeResolver.Resolve<IExecutionRecorder>();
            return recorder.Events.ToArray();
        }
        finally
        {
            scopeResolver.Dispose();
            rootResolver.Dispose();
        }
    }

    private static (IObjectResolver Root, IObjectResolver Scope) CreateResolver(
        Action<IContainerBuilder> registerServices,
        Func<IObjectResolver, IReadOnlyList<Registration>> getInitializableRegistrations)
    {
        if (registerServices == null) throw new ArgumentNullException(nameof(registerServices));
        if (getInitializableRegistrations == null) throw new ArgumentNullException(nameof(getInitializableRegistrations));

        var rootBuilder = new ContainerBuilder();
        rootBuilder.Register<ExecutionRecorder>(Lifetime.Singleton).As<IExecutionRecorder>();
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

    private abstract class RecordingInitializableBase : IInitializable
    {
        private readonly IExecutionRecorder _recorder;
        private readonly string _eventName;

        protected RecordingInitializableBase(IExecutionRecorder recorder, string eventName)
        {
            _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _eventName = eventName ?? throw new ArgumentNullException(nameof(eventName));
        }

        public void Initialize()
        {
            _recorder.Record(_eventName);
        }
    }

    private abstract class RecordingAsyncInitializableBase : RecordingInitializableBase
    {
        protected RecordingAsyncInitializableBase(IExecutionRecorder recorder, string eventName)
            : base(recorder, eventName)
        {
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
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
    private interface IManagedDualTag { }
    private interface IPlainTag { }

    private sealed class PreBootstrapStageInitializable :
        RecordingAsyncInitializableBase,
        IPreBootstrapStartupInitializableService,
        IPreBootstrapTag
    {
        public PreBootstrapStageInitializable(IExecutionRecorder recorder)
            : base(recorder, "prebootstrap")
        {
        }
    }

    private sealed class PlatformStageInitializable :
        RecordingAsyncInitializableBase,
        IPlatformStartupInitializableService,
        IPlatformTag
    {
        public PlatformStageInitializable(IExecutionRecorder recorder)
            : base(recorder, "platform")
        {
        }
    }

    private sealed class ContentStageInitializable :
        RecordingAsyncInitializableBase,
        IContentStartupInitializableService,
        IContentTag
    {
        public ContentStageInitializable(IExecutionRecorder recorder)
            : base(recorder, "content")
        {
        }
    }

    private sealed class SessionStageInitializable :
        RecordingAsyncInitializableBase,
        ISessionStartupInitializableService,
        ISessionTag
    {
        public SessionStageInitializable(IExecutionRecorder recorder)
            : base(recorder, "session")
        {
        }
    }

    private sealed class UiStageInitializable :
        RecordingAsyncInitializableBase,
        IUiStartupInitializableService,
        IUiTag
    {
        public UiStageInitializable(IExecutionRecorder recorder)
            : base(recorder, "ui")
        {
        }
    }

    private sealed class NonStagedInitializable : RecordingInitializableBase, INonStagedTag
    {
        public NonStagedInitializable(IExecutionRecorder recorder)
            : base(recorder, "nonstaged")
        {
        }
    }

    private sealed class DuplicateSessionStageInitializable :
        RecordingAsyncInitializableBase,
        ISessionStartupInitializableService,
        IDuplicateTagA,
        IDuplicateTagB
    {
        public DuplicateSessionStageInitializable(IExecutionRecorder recorder)
            : base(recorder, "duplicate")
        {
        }
    }

    private sealed class ManagedDualInitializable :
        RecordingAsyncInitializableBase,
        ISessionInitializableService,
        IManagedDualTag
    {
        public ManagedDualInitializable(IExecutionRecorder recorder)
            : base(recorder, "managed-dual")
        {
        }
    }

    private sealed class PlainInitializable : RecordingInitializableBase, IPlainTag
    {
        public PlainInitializable(IExecutionRecorder recorder)
            : base(recorder, "plain")
        {
        }
    }
}
