using SignalGrid.Ampelum.Audit;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Audit;

public sealed class AuditPrimitiveValidatorTests
{
    [Fact]
    public void Validate_StringLongDoubleBoolNull_Pass()
    {
        var d = new Dictionary<string, object?>
        {
            ["s"] = "x", ["i"] = 42L, ["d"] = 3.14, ["b"] = true, ["n"] = null
        };
        Should.NotThrow(() => AuditPrimitiveValidator.Validate("Inputs", d));
    }

    [Fact]
    public void Validate_NestedDictAndList_Pass()
    {
        var d = new Dictionary<string, object?>
        {
            ["o"] = new Dictionary<string, object?> { ["k"] = "v" },
            ["a"] = new List<object?> { 1L, "x", null }
        };
        Should.NotThrow(() => AuditPrimitiveValidator.Validate("Inputs", d));
    }

    [Fact]
    public void Validate_IntPromotedToLong_Pass()
    {
        var d = new Dictionary<string, object?> { ["i"] = 7 };
        Should.NotThrow(() => AuditPrimitiveValidator.Validate("Inputs", d));
    }

    [Fact]
    public void Validate_CustomRecord_ThrowsWithKeyPath()
    {
        var d = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?>
            {
                ["address"] = new { Street = "x" }
            }
        };
        var ex = Should.Throw<AuditSerializationException>(
            () => AuditPrimitiveValidator.Validate("Inputs", d));
        ex.Message.ShouldContain("Inputs.user.address");
    }

    [Fact]
    public void Validate_ListContainingNonPrimitive_ThrowsWithIndexPath()
    {
        var d = new Dictionary<string, object?>
        {
            ["arr"] = new List<object?> { "ok", new { Bad = 1 } }
        };
        var ex = Should.Throw<AuditSerializationException>(
            () => AuditPrimitiveValidator.Validate("Outputs", d));
        ex.Message.ShouldContain("Outputs.arr[1]");
    }
}
