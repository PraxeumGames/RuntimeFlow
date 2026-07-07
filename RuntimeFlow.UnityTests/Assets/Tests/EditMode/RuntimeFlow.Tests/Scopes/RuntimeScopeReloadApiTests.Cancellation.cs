using NUnit.Framework;
using System.Threading.Tasks;

namespace RuntimeFlow.Tests
{

public sealed partial class RuntimeScopeReloadApiTests
{
    [Test]
    public async Task ReloadScopeAsync_CallerCancellation_PublishesCanceledProgressPerOperationKind()
    {
        await AssertCanceledProgressForSessionReloadAsync();
        await AssertCanceledProgressForSceneReloadAsync();
        await AssertCanceledProgressForModuleReloadAsync();
    }
}

}
