using System.Linq;
using System.Reflection;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

public sealed partial class UxImprovementsTests
{
    [Fact]
    public void OperationCodes_AllConstantsAreNonNullNonEmpty()
    {
        var fields = typeof(RuntimeOperationCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string));

        foreach (var field in fields)
        {
            var value = (string?)field.GetValue(null);
            Assert.False(string.IsNullOrEmpty(value),
                $"Operation code '{field.Name}' must be non-null and non-empty.");
        }
    }

    [Fact]
    public void OperationCodes_AllConstantsHaveUniqueValues()
    {
        var values = typeof(RuntimeOperationCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string?)f.GetValue(null))
            .ToArray();

        Assert.Equal(values.Length, values.Distinct().Count());
    }
}
