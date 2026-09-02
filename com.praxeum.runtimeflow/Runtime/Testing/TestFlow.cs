using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VContainer;

namespace RuntimeFlow.Testing
{
    /// <summary>
    /// A headless <see cref="RuntimeFlowHost"/> for tests: the production installers are used as they
    /// are, single services are swapped for fakes, timeouts are off, framework logs are captured, and
    /// startup is bounded by a deadline that reports what was still running.
    /// <code>
    /// await using var app = await TestFlow.Create(GlobalInstaller.Register, SessionInstaller.Register)
    ///     .Override&lt;IProfileApi&gt;(new FakeProfileApi())
    ///     .StartAsync();
    /// Assert.That(app.Resolve&lt;PlayerProfile&gt;().Coins, Is.EqualTo(10));
    /// </code>
    /// </summary>
    public sealed class TestFlow : IAsyncDisposable
    {
        private readonly Action<IContainerBuilder> _globalInstaller;
        private readonly Action<IContainerBuilder> _sessionInstaller;
        private readonly List<ServiceOverride> _overrides = new List<ServiceOverride>();
        private readonly List<string> _visitedScopes = new List<string>();
        private readonly RuntimeFlowOptions _options;
        private readonly CapturingLogger _log = new CapturingLogger();

        private RuntimeFlowHost? _host;
        private TimeSpan _startupTimeout = TimeSpan.FromSeconds(30);

        private TestFlow(Action<IContainerBuilder> global, Action<IContainerBuilder> session)
        {
            _globalInstaller = global ?? throw new ArgumentNullException(nameof(global));
            _sessionInstaller = session ?? throw new ArgumentNullException(nameof(session));
            _options = new RuntimeFlowOptions
            {
                Logger = _log,
                TimeoutMultiplier = 0,
                StallWarningAfter = TimeSpan.FromSeconds(2),

                // Tests must not sit for the production five seconds while an abandoned service drains;
                // a fake that ignores its token should cost a test 200 ms, not a timed-out fixture.
                CancellationGrace = TimeSpan.FromMilliseconds(200)
            };
        }

        /// <summary>Prepares a host around the two production installers; nothing is built until <see cref="StartAsync"/>.</summary>
        public static TestFlow Create(Action<IContainerBuilder> global, Action<IContainerBuilder> session)
            => new TestFlow(global, session);

        /// <summary>
        /// Replaces every registration exposing <typeparamref name="T"/> — in either installer — with
        /// <paramref name="instance"/>, which also joins the initialization graph when it is an
        /// <see cref="IAsyncInitializable"/>.
        /// </summary>
        public TestFlow Override<T>(T instance) where T : class
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            return AddOverride(typeof(T), builder =>
            {
                var registration = builder.RegisterInstance(instance).As(typeof(T));
                if (instance is IAsyncInitializable && typeof(T) != typeof(IAsyncInitializable))
                    registration.As<IAsyncInitializable>();
            });
        }

        /// <summary>
        /// Replaces every registration exposing <typeparamref name="T"/> with a singleton
        /// <typeparamref name="TImpl"/> the container constructs itself.
        /// </summary>
        public TestFlow Override<T, TImpl>() where TImpl : class, T
        {
            return AddOverride(typeof(T), builder =>
            {
                var registration = builder.Register<TImpl>(Lifetime.Singleton).As<T>();
                if (typeof(IAsyncInitializable).IsAssignableFrom(typeof(TImpl)) && typeof(T) != typeof(IAsyncInitializable))
                    registration.As<IAsyncInitializable>();
            });
        }

        /// <summary>Adjusts the options the host runs with; the capturing logger is replaced if you set one.</summary>
        public TestFlow Configure(Action<RuntimeFlowOptions> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            ThrowIfStarted();
            configure(_options);
            return this;
        }

        /// <summary>Adds an observer, for example a <see cref="CollectingObserver"/>.</summary>
        public TestFlow ObserveWith(IRuntimeFlowObserver observer)
        {
            if (observer == null) throw new ArgumentNullException(nameof(observer));
            ThrowIfStarted();
            _options.Observers.Add(observer);
            return this;
        }

        /// <summary>Deadline for <see cref="StartAsync"/>; the default is 30 seconds.</summary>
        public TestFlow WithStartupTimeout(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            ThrowIfStarted();
            _startupTimeout = timeout;
            return this;
        }

        /// <summary>
        /// Builds both scopes and runs their graphs, then returns this instance so it can be awaited and
        /// disposed in one expression.
        /// </summary>
        /// <exception cref="TimeoutException">Startup exceeded <see cref="WithStartupTimeout"/>; the message carries the status.</exception>
        /// <exception cref="InvalidOperationException">An override matched no registration in any scope.</exception>
        public async Task<TestFlow> StartAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfStarted();
            _host = new RuntimeFlowHost(
                builder => Install(builder, _globalInstaller, "global"),
                builder => Install(builder, _sessionInstaller, "session"),
                _options);

            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    var startup = _host.StartAsync(deadline.Token);
                    var expiry = Task.Delay(_startupTimeout, deadline.Token);
                    var finished = await Task.WhenAny(startup, expiry);
                    if (!ReferenceEquals(finished, startup))
                    {
                        var status = _host.GetStatus();
                        deadline.Cancel();
                        Forget(startup);
                        throw new TimeoutException(
                            $"Startup did not finish within {Seconds(_startupTimeout)}. {Render(status)}");
                    }

                    deadline.Cancel();
                    Forget(expiry);
                    Result = await startup;
                }
            }
            catch (Exception)
            {
                // A failed startup still built containers, and an override mismatch throws before the
                // global scope was ever handed back: without this the test would leak both.
                await _host.DisposeAsync();
                throw;
            }

            return this;
        }

        /// <summary>The host under test; valid after <see cref="StartAsync"/>.</summary>
        public RuntimeFlowHost Host => _host
            ?? throw new InvalidOperationException("Await TestFlow.StartAsync before using the host.");

        /// <summary>The global container.</summary>
        public IObjectResolver Global => Host.Global;

        /// <summary>The current session scope.</summary>
        public IScopedObjectResolver Session => Host.Session;

        /// <summary>Result of the startup run; null until <see cref="StartAsync"/> returned.</summary>
        public StartupResult? Result { get; private set; }

        /// <summary>Every framework log line, formatted as "{Level}: {message}".</summary>
        public IReadOnlyList<string> Log => _log.Lines;

        /// <summary>Resolves a service from the session scope (which sees the global one).</summary>
        public T Resolve<T>() => Session.Resolve<T>();

        /// <summary>Disposes the host, tearing both scopes down in order.</summary>
        public async ValueTask DisposeAsync()
        {
            if (_host == null) return;
            await _host.DisposeAsync();
        }

        private TestFlow AddOverride(Type serviceType, Action<IContainerBuilder> register)
        {
            ThrowIfStarted();
            foreach (var existing in _overrides)
            {
                if (existing.ServiceType == serviceType)
                    throw new InvalidOperationException($"{serviceType.Name} is already overridden.");
            }
            _overrides.Add(new ServiceOverride(serviceType, register));
            return this;
        }

        private void Install(IContainerBuilder builder, Action<IContainerBuilder> installer, string scope)
        {
            if (!_visitedScopes.Contains(scope)) _visitedScopes.Add(scope);

            var wrapper = new OverridingContainerBuilder(builder, _overrides, _options.Logger, scope);
            installer(wrapper);
            wrapper.Flush();

            if (scope == "session") VerifyOverrides();
        }

        private void VerifyOverrides()
        {
            foreach (var candidate in _overrides)
            {
                if (candidate.MatchedScopes.Count > 0) continue;
                throw new InvalidOperationException(
                    $"Override<{candidate.ServiceType.Name}> matched no registration: no installer registers a service " +
                    $"exposing {candidate.ServiceType.Name} in the scopes checked ({string.Join(", ", _visitedScopes)}). " +
                    "Override a type one of the installers registers.");
            }
        }

        private void ThrowIfStarted()
        {
            if (_host != null) throw new InvalidOperationException("TestFlow.StartAsync has already been called.");
        }

        private static void Forget(Task task)
            => task.ContinueWith(finished => _ = finished.Exception, TaskContinuationOptions.OnlyOnFaulted);

        private static string Render(RuntimeFlowStatus status)
        {
            var text = new StringBuilder();
            text.Append(status.Scope).Append(": ").Append(status.State).Append(' ')
                .Append(status.CompletedCount.ToString(CultureInfo.InvariantCulture)).Append('/')
                .Append(status.TotalCount.ToString(CultureInfo.InvariantCulture)).Append(" (")
                .Append(status.Percent.ToString("0.0", CultureInfo.InvariantCulture)).Append("%).");

            var running = new List<string>();
            var blocked = new List<string>();
            foreach (var service in status.Services)
            {
                if (service.State == ServiceState.Running)
                    running.Add($"{service.Name} ({Seconds(service.Elapsed)})");
                else if (service.State == ServiceState.Pending && service.WaitingOn.Count > 0)
                    blocked.Add($"{service.Name} (waits for {string.Join(", ", service.WaitingOn)})");
            }

            if (running.Count > 0) text.Append(" Running: ").Append(string.Join(", ", running)).Append('.');
            if (blocked.Count > 0) text.Append(" Blocked: ").Append(string.Join(", ", blocked)).Append('.');
            if (running.Count == 0 && blocked.Count == 0) text.Append(" Nothing was running.");
            return text.ToString();
        }

        private static string Seconds(TimeSpan value)
            => value.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
    }
}
