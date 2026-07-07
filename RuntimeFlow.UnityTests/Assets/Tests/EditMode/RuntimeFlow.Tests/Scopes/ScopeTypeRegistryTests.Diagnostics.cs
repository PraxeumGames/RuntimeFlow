using NUnit.Framework;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{

public sealed partial class ScopeTypeRegistryTests
{
    [Test]
    public void DefineScope_DuplicateDeclaration_ThrowsDeterministicDiagnostic()
    {
        var builder = new GameContextBuilder();
        builder.Scene<SceneScope>();

        var exception = Assert.Throws<ScopeRegistrationException>(() => builder.Scene<SceneScope>());

        Assert.AreEqual("GBSR3001", exception.DiagnosticCode);
    }

    [Test]
    public void DefineScope_ConflictingDeclaration_ThrowsDeterministicDiagnostic()
    {
        var builder = new GameContextBuilder();
        builder.Scene<SharedScope>();

        var exception = Assert.Throws<ScopeRegistrationException>(() => builder.Module<SharedScope>());

        Assert.AreEqual("GBSR3002", exception.DiagnosticCode);
    }
}

}
