using System.Reflection;
using SignalGrid.Ampelum.Tools;
using SignalGrid.Ampelum.Tools.CodeInterpreter;
using Shouldly;
using Xunit;

namespace Ampelum.Tools.CodeInterpreter.Tests;

public sealed class CodeInterpreterToolTests
{
    [Fact]
    public async Task CodeInterpreter_Computes_Sum_OfRange()
    {
        var tool = new CodeInterpreterTool("Enumerable.Range(1, 10).Sum()");
        var r = await tool.InvokeAsync(TestContext.Current.CancellationToken);
        r.Value.ShouldBe(55);
        r.Display.ShouldBe("55");
    }

    [Fact]
    public async Task CodeInterpreter_NullExpression_YieldsEmptyDisplay()
    {
        var tool = new CodeInterpreterTool("(string?)null");
        var r = await tool.InvokeAsync(TestContext.Current.CancellationToken);
        r.Value.ShouldBeNull();
        r.Display.ShouldBe("");
    }

    [Fact]
    public void NameDescription_NonEmpty_AndCodePropExposed()
    {
        var tool = new CodeInterpreterTool("1+1");
        tool.Name.ShouldBe("code_interpreter");
        tool.Description.ShouldNotBeNullOrWhiteSpace();
        tool.Code.ShouldBe("1+1");
    }

    [Fact]
    public void HasToolCategory_Compute()
    {
        var attr = typeof(CodeInterpreterTool).GetCustomAttribute<ToolCategoryAttribute>();
        attr.ShouldNotBeNull();
        attr!.Kind.ShouldBe(ToolCategoryKind.Compute);
    }
}
