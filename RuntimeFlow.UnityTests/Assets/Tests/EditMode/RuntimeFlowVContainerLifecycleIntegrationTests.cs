using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Contexts;
using UnityEngine.TestTools;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.UnityIntegrationTests
{
    public sealed class RuntimeFlowVContainerLifecycleIntegrationTests
    {
        [UnityTest]
        public IEnumerator InitializeAsync_UsesRealVContainer_ForEntryPointLifecycleOrder()
        {
            var events = new List<string>();
            var mainThreadId = Thread.CurrentThread.ManagedThreadId;
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.Global().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterGlobalBootstrapPreset(containerBuilder);
                    containerBuilder.RegisterInstance(events);
                    containerBuilder.RegisterInstance(new MainThreadProbe(mainThreadId));
                    containerBuilder.Register<GlobalInitializable>(Lifetime.Singleton).As<IInitializable>();
                    containerBuilder.Register<GlobalAsyncService>(Lifetime.Singleton).AsImplementedInterfaces();
                    containerBuilder.Register<GlobalStartable>(Lifetime.Singleton).As<IStartable>();
                });
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterSessionBootstrapPreset(containerBuilder);
                    containerBuilder.Register<SessionInitializable>(Lifetime.Singleton).As<IInitializable>();
                    containerBuilder.Register<SessionDualService>(Lifetime.Singleton).AsImplementedInterfaces();
                    containerBuilder.Register<SessionAsyncService>(Lifetime.Singleton).AsImplementedInterfaces();
                    containerBuilder.Register<SessionStartable>(Lifetime.Singleton).As<IStartable>();
                });
            });

            yield return AwaitTask(pipeline.InitializeAsync());

            AssertBefore(events, "global:init", "global:async");
            AssertBefore(events, "global:async", "global:start:main-thread");
            AssertBefore(events, "global:start:main-thread", "session:init");
            AssertBefore(events, "session:init", "session:async");
            AssertBefore(events, "dual:init", "dual:async");
            AssertBefore(events, "session:async", "session:start:main-thread");
            AssertBefore(events, "dual:async", "dual:start:main-thread");
            Assert.Contains("global:start:main-thread", events);
            Assert.Contains("session:start:main-thread", events);
            Assert.Contains("dual:start:main-thread", events);

            yield return AwaitTask(pipeline.DisposeAsync().AsTask());
        }

        [UnityTest]
        public IEnumerator InitializeAsync_WhenRealVContainerStartableThrows_DisposesInitializedAsyncServices()
        {
            var events = new List<string>();
            var pipeline = RuntimePipeline.Create(builder =>
            {
                builder.Global().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterGlobalBootstrapPreset(containerBuilder);
                });
                builder.Session().ConfigureContainer(containerBuilder =>
                {
                    RuntimeFlowInstallerModules.RegisterSessionBootstrapPreset(containerBuilder);
                    containerBuilder.RegisterInstance(events);
                    containerBuilder.Register<DisposableSessionAsyncService>(Lifetime.Singleton).AsImplementedInterfaces();
                    containerBuilder.Register<ThrowingStartable>(Lifetime.Singleton).As<IStartable>();
                });
            });

            yield return AwaitFaulted<InvalidOperationException>(pipeline.InitializeAsync(), "start failed");

            Assert.Contains("async:init", events);
            Assert.Contains("start:throw", events);
            Assert.Contains("async:dispose", events);
            AssertBefore(events, "async:init", "start:throw");
            AssertBefore(events, "start:throw", "async:dispose");
        }

        private static IEnumerator AwaitTask(Task task, float timeoutSeconds = 10f)
        {
            var timeoutAt = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
            while (!task.IsCompleted)
            {
                if (DateTime.UtcNow >= timeoutAt)
                    Assert.Fail($"Task did not complete within {timeoutSeconds:F1}s.");

                yield return null;
            }

            if (task.IsCanceled)
                throw new OperationCanceledException("Task was canceled.");

            if (task.Exception != null)
                ExceptionDispatchInfo.Capture(task.Exception.GetBaseException()).Throw();
        }

        private static IEnumerator AwaitFaulted<TException>(Task task, string expectedMessage, float timeoutSeconds = 10f)
            where TException : Exception
        {
            var timeoutAt = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
            while (!task.IsCompleted)
            {
                if (DateTime.UtcNow >= timeoutAt)
                    Assert.Fail($"Task did not complete within {timeoutSeconds:F1}s.");

                yield return null;
            }

            Assert.That(task.IsFaulted, Is.True, "Task should fault.");
            var exception = task.Exception!.GetBaseException();
            Assert.That(exception, Is.TypeOf<TException>());
            Assert.That(exception.Message, Is.EqualTo(expectedMessage));
        }

        private static void AssertBefore(IReadOnlyList<string> events, string expectedEarlier, string expectedLater)
        {
            var earlier = events.ToList().IndexOf(expectedEarlier);
            var later = events.ToList().IndexOf(expectedLater);
            Assert.That(earlier, Is.GreaterThanOrEqualTo(0), $"Missing '{expectedEarlier}'. Events: {string.Join(", ", events)}");
            Assert.That(later, Is.GreaterThanOrEqualTo(0), $"Missing '{expectedLater}'. Events: {string.Join(", ", events)}");
            Assert.That(earlier, Is.LessThan(later), $"Expected '{expectedEarlier}' before '{expectedLater}'. Events: {string.Join(", ", events)}");
        }

        private sealed class MainThreadProbe
        {
            public MainThreadProbe(int threadId)
            {
                ThreadId = threadId;
            }

            public int ThreadId { get; }
            public bool IsMainThread => Thread.CurrentThread.ManagedThreadId == ThreadId;
        }

        private sealed class GlobalInitializable : IInitializable
        {
            private readonly List<string> _events;

            public GlobalInitializable(List<string> events) => _events = events;

            public void Initialize() => _events.Add("global:init");
        }

        private sealed class GlobalAsyncService : IGlobalInitializableService
        {
            private readonly List<string> _events;

            public GlobalAsyncService(List<string> events) => _events = events;

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _events.Add("global:async");
                return Task.CompletedTask;
            }
        }

        private sealed class GlobalStartable : IStartable
        {
            private readonly List<string> _events;
            private readonly MainThreadProbe _mainThread;

            public GlobalStartable(List<string> events, MainThreadProbe mainThread)
            {
                _events = events;
                _mainThread = mainThread;
            }

            public void Start() => _events.Add(_mainThread.IsMainThread ? "global:start:main-thread" : "global:start:wrong-thread");
        }

        private sealed class SessionInitializable : IInitializable
        {
            private readonly List<string> _events;

            public SessionInitializable(List<string> events) => _events = events;

            public void Initialize() => _events.Add("session:init");
        }

        private sealed class SessionDualService : ISessionInitializableService, IInitializable, IStartable
        {
            private readonly List<string> _events;
            private readonly MainThreadProbe _mainThread;

            public SessionDualService(List<string> events, MainThreadProbe mainThread)
            {
                _events = events;
                _mainThread = mainThread;
            }

            public void Initialize() => _events.Add("dual:init");

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _events.Add("dual:async");
                return Task.CompletedTask;
            }

            public void Start() => _events.Add(_mainThread.IsMainThread ? "dual:start:main-thread" : "dual:start:wrong-thread");
        }

        private sealed class SessionAsyncService : ISessionInitializableService
        {
            private readonly List<string> _events;

            public SessionAsyncService(List<string> events) => _events = events;

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _events.Add("session:async");
                return Task.CompletedTask;
            }
        }

        private sealed class SessionStartable : IStartable
        {
            private readonly List<string> _events;
            private readonly MainThreadProbe _mainThread;

            public SessionStartable(List<string> events, MainThreadProbe mainThread)
            {
                _events = events;
                _mainThread = mainThread;
            }

            public void Start() => _events.Add(_mainThread.IsMainThread ? "session:start:main-thread" : "session:start:wrong-thread");
        }

        private sealed class DisposableSessionAsyncService : ISessionInitializableService, IAsyncDisposableService
        {
            private readonly List<string> _events;

            public DisposableSessionAsyncService(List<string> events) => _events = events;

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _events.Add("async:init");
                return Task.CompletedTask;
            }

            public Task DisposeAsync(CancellationToken cancellationToken)
            {
                _events.Add("async:dispose");
                return Task.CompletedTask;
            }
        }

        private sealed class ThrowingStartable : IStartable
        {
            private readonly List<string> _events;

            public ThrowingStartable(List<string> events) => _events = events;

            public void Start()
            {
                _events.Add("start:throw");
                throw new InvalidOperationException("start failed");
            }
        }
    }
}
