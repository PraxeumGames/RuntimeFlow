using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

public sealed partial class ScopeTypeRegistryTests
{
    private static void AssertScopeMapping(GameContextBuilder builder, Type scopeType, GameContextType expectedScope)
    {
        var found = builder.TryResolveScopeType(scopeType, out var actualScope);
        Assert.True(found);
        Assert.Equal(expectedScope, actualScope);
    }

    private sealed class FluentSceneService : ITestSceneService
    {
        private static int _attempts;

        public static int Attempts => Volatile.Read(ref _attempts);

        int ITestSceneService.Attempts => Volatile.Read(ref _attempts);

        public static void Reset()
        {
            Interlocked.Exchange(ref _attempts, 0);
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return Task.CompletedTask;
        }
    }

    private sealed class FluentSessionService : ITestSessionService
    {
        private static int _attempts;

        public static int Attempts => Volatile.Read(ref _attempts);

        int ITestSessionService.Attempts => Volatile.Read(ref _attempts);

        public static void Reset()
        {
            Interlocked.Exchange(ref _attempts, 0);
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return Task.CompletedTask;
        }
    }

    private sealed class FluentModuleService : ITestModuleService
    {
        private static int _attempts;

        public static int Attempts => Volatile.Read(ref _attempts);

        int ITestModuleService.Attempts => Volatile.Read(ref _attempts);

        public static void Reset()
        {
            Interlocked.Exchange(ref _attempts, 0);
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return Task.CompletedTask;
        }
    }

    private sealed class OpenGenericFluentSceneService<T> where T : class { }

    private sealed class SceneScope : ISceneScope
    {
        private readonly Action<IGameScopeRegistrationBuilder>? _configure;

        public SceneScope() { }

        public SceneScope(Action<IGameScopeRegistrationBuilder> configure)
            => _configure = configure;

        public void Configure(IGameScopeRegistrationBuilder builder)
            => _configure?.Invoke(builder);
    }

    private sealed class SecondarySceneScope : ISceneScope
    {
        private readonly Action<IGameScopeRegistrationBuilder>? _configure;

        public SecondarySceneScope() { }

        public SecondarySceneScope(Action<IGameScopeRegistrationBuilder> configure)
            => _configure = configure;

        public void Configure(IGameScopeRegistrationBuilder builder)
            => _configure?.Invoke(builder);
    }

    private sealed class ModuleScope : IModuleScope
    {
        private readonly Action<IGameScopeRegistrationBuilder>? _configure;

        public ModuleScope() { }

        public ModuleScope(Action<IGameScopeRegistrationBuilder> configure)
            => _configure = configure;

        public void Configure(IGameScopeRegistrationBuilder builder)
            => _configure?.Invoke(builder);
    }

    private sealed class SecondaryModuleScope : IModuleScope
    {
        private readonly Action<IGameScopeRegistrationBuilder>? _configure;

        public SecondaryModuleScope() { }

        public SecondaryModuleScope(Action<IGameScopeRegistrationBuilder> configure)
            => _configure = configure;

        public void Configure(IGameScopeRegistrationBuilder builder)
            => _configure?.Invoke(builder);
    }

    private sealed class SharedScope : ISceneScope, IModuleScope
    {
        public void Configure(IGameScopeRegistrationBuilder builder) { }
    }
}
