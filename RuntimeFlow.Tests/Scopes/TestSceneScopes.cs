using System;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

internal sealed class TestGlobalScope { }
internal sealed class TestSessionScope { }

internal sealed class TestSceneScope : ISceneScope
{
    private readonly Action<IGameScopeRegistrationBuilder>? _configure;

    public TestSceneScope() { }

    public TestSceneScope(Action<IGameScopeRegistrationBuilder> configure)
        => _configure = configure;

    public void Configure(IGameScopeRegistrationBuilder builder)
        => _configure?.Invoke(builder);
}

internal sealed class TestModuleScope : IModuleScope
{
    private readonly Action<IGameScopeRegistrationBuilder>? _configure;

    public TestModuleScope() { }

    public TestModuleScope(Action<IGameScopeRegistrationBuilder> configure)
        => _configure = configure;

    public void Configure(IGameScopeRegistrationBuilder builder)
        => _configure?.Invoke(builder);
}

internal sealed class FallbackSceneScope : ISceneScope
{
    private readonly Action<IGameScopeRegistrationBuilder>? _configure;

    public FallbackSceneScope() { }

    public FallbackSceneScope(Action<IGameScopeRegistrationBuilder> configure)
        => _configure = configure;

    public void Configure(IGameScopeRegistrationBuilder builder)
        => _configure?.Invoke(builder);
}
