using System.Collections.Generic;
using SignalGrid.Ampelum.Safety.Tools;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Safety.Tools;

public sealed class ToolGatePolicyTests
{
    [Fact]
    public void Open_allows_everything()
    {
        ToolGatePolicy.Open.IsAllowed("any", ToolCategory.SystemControl).ShouldBeTrue();
    }

    [Fact]
    public void Deny_set_blocks_named_tool()
    {
        var p = new ToolGatePolicy(Deny: new HashSet<string> { "delete_db" });
        p.IsAllowed("delete_db", ToolCategory.DataMutation).ShouldBeFalse();
        p.IsAllowed("read_db", ToolCategory.DataAccess).ShouldBeTrue();
    }

    [Fact]
    public void DenyCategories_blocks_category()
    {
        var p = new ToolGatePolicy(DenyCategories: new HashSet<ToolCategory> { ToolCategory.SystemControl });
        p.IsAllowed("shell_exec", ToolCategory.SystemControl).ShouldBeFalse();
    }

    [Fact]
    public void Allow_set_blocks_unknown_tool()
    {
        var p = new ToolGatePolicy(Allow: new HashSet<string> { "search" });
        p.IsAllowed("search", ToolCategory.Information).ShouldBeTrue();
        p.IsAllowed("rm_rf", ToolCategory.SystemControl).ShouldBeFalse();
    }

    [Fact]
    public void AllowCategories_blocks_unknown_category()
    {
        var p = new ToolGatePolicy(AllowCategories: new HashSet<ToolCategory> { ToolCategory.Information });
        p.IsAllowed("search", ToolCategory.Information).ShouldBeTrue();
        p.IsAllowed("calc", ToolCategory.Computation).ShouldBeFalse();
    }

    [Fact]
    public void Deny_overrides_Allow_for_same_tool()
    {
        var p = new ToolGatePolicy(
            Allow: new HashSet<string> { "search" },
            Deny: new HashSet<string> { "search" });
        p.IsAllowed("search", ToolCategory.Information).ShouldBeFalse();
    }
}
