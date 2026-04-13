using RuntimeFlow.Contexts;
using Xunit;

namespace RuntimeFlow.Tests;

public sealed partial class ScopeTypeRegistryTests
{
    [Fact]
    public void DefineScope_DuplicateDeclaration_ThrowsDeterministicDiagnostic()
    {
        var builder = new GameContextBuilder();
        builder.Scene<SceneScope>();

        var exception = Assert.Throws<ScopeRegistrationException>(() => builder.Scene<SceneScope>());

        Assert.Equal("GBSR3001", exception.DiagnosticCode);
    }

    [Fact]
    public void DefineScope_ConflictingDeclaration_ThrowsDeterministicDiagnostic()
    {
        var builder = new GameContextBuilder();
        builder.Scene<SharedScope>();

        var exception = Assert.Throws<ScopeRegistrationException>(() => builder.Module<SharedScope>());

        Assert.Equal("GBSR3002", exception.DiagnosticCode);
    }
}
