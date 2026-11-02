using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Agents.Tools;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Agents.Tools;

public class AgentToolRegistryTests
{
    sealed class FakeTool(string name, string version, string category) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => "fake";
        public string Category { get; } = category;
        public string Version { get; } = version;
        public ValueTask<AgentToolResult> InvokeAsync(CancellationToken ct) =>
            ValueTask.FromResult(new AgentToolResult(true, "ok", null, new Dictionary<string, string>()));
    }

    [Fact]
    public void Register_and_find_by_name_version()
    {
        var reg = new AgentToolRegistry();
        var t1 = new FakeTool("alpha", "1.0", "io");
        reg.Register(t1);
        reg.Find("alpha", "1.0").ShouldBe(t1);
        reg.Find("alpha", "2.0").ShouldBeNull();
    }

    [Fact]
    public void Duplicate_name_version_throws()
    {
        var reg = new AgentToolRegistry();
        reg.Register(new FakeTool("a", "1.0", "io"));
        Should.Throw<System.InvalidOperationException>(() =>
            reg.Register(new FakeTool("a", "1.0", "io")));
    }

    [Fact]
    public void Different_versions_coexist()
    {
        var reg = new AgentToolRegistry();
        reg.Register(new FakeTool("a", "1.0", "io"));
        reg.Register(new FakeTool("a", "2.0", "io"));
        reg.Find("a", "1.0").ShouldNotBeNull();
        reg.Find("a", "2.0").ShouldNotBeNull();
    }

    [Fact]
    public void List_by_category()
    {
        var reg = new AgentToolRegistry();
        reg.Register(new FakeTool("a", "1.0", "io"));
        reg.Register(new FakeTool("b", "1.0", "math"));
        reg.Register(new FakeTool("c", "1.0", "io"));
        var ios = reg.ListByCategory("io");
        ios.Count.ShouldBe(2);
        var maths = reg.ListByCategory("math");
        maths.Count.ShouldBe(1);
        reg.ListByCategory("missing").Count.ShouldBe(0);
    }

    [Fact]
    public void All_returns_snapshot()
    {
        var reg = new AgentToolRegistry();
        reg.Register(new FakeTool("a", "1.0", "x"));
        reg.Register(new FakeTool("b", "1.0", "y"));
        reg.All().Count.ShouldBe(2);
    }

    [Fact]
    public void Register_null_throws()
    {
        var reg = new AgentToolRegistry();
        Should.Throw<System.ArgumentNullException>(() => reg.Register(null!));
    }

    [Fact]
    public void Find_null_args_throws()
    {
        var reg = new AgentToolRegistry();
        Should.Throw<System.ArgumentNullException>(() => reg.Find(null!, "1.0"));
        Should.Throw<System.ArgumentNullException>(() => reg.Find("x", null!));
    }

    [Fact]
    public void List_by_category_null_throws()
    {
        var reg = new AgentToolRegistry();
        Should.Throw<System.ArgumentNullException>(() => reg.ListByCategory(null!));
    }
}
