using System.Linq;
using SignalGrid.Ampelum.Safety.Tools;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Safety.Tools;

public sealed class InMemoryToolRegistryTests
{
    [Fact]
    public void Register_then_TryGet_returns_registration()
    {
        var sut = new InMemoryToolRegistry();
        var reg = new ToolRegistration("search", "1.0.0", ToolCategory.Information);
        sut.Register(reg);
        sut.TryGet("search", "1.0.0", out var got).ShouldBeTrue();
        got.ShouldBe(reg);
    }

    [Fact]
    public void TryGet_miss_returns_false_and_null()
    {
        var sut = new InMemoryToolRegistry();
        sut.TryGet("nope", "1", out var got).ShouldBeFalse();
        got.ShouldBeNull();
    }

    [Fact]
    public void Register_duplicate_throws()
    {
        var sut = new InMemoryToolRegistry();
        sut.Register(new ToolRegistration("t", "1", ToolCategory.Information));
        Should.Throw<System.InvalidOperationException>(
            () => sut.Register(new ToolRegistration("t", "1", ToolCategory.Information)));
    }

    [Fact]
    public void Register_distinct_versions_coexist()
    {
        var sut = new InMemoryToolRegistry();
        sut.Register(new ToolRegistration("t", "1", ToolCategory.Information));
        sut.Register(new ToolRegistration("t", "2", ToolCategory.Information));
        sut.GetAll().Count().ShouldBe(2);
    }

    [Fact]
    public void GetAll_returns_all_registered()
    {
        var sut = new InMemoryToolRegistry();
        sut.Register(new ToolRegistration("a", "1", ToolCategory.Information));
        sut.Register(new ToolRegistration("b", "1", ToolCategory.Computation));
        var all = sut.GetAll().ToList();
        all.Count.ShouldBe(2);
    }
}
