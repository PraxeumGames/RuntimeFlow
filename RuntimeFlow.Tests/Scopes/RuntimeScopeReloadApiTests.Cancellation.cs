using System.Threading.Tasks;

namespace RuntimeFlow.Tests;

public sealed partial class RuntimeScopeReloadApiTests
{
    [Fact]
    public async Task ReloadScopeAsync_CallerCancellation_PublishesCanceledProgressPerOperationKind()
    {
        await AssertCanceledProgressForSessionReloadAsync();
        await AssertCanceledProgressForSceneReloadAsync();
        await AssertCanceledProgressForModuleReloadAsync();
    }
}
