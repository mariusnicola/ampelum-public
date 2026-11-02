using System.Reflection;
using SignalGrid.Ampelum.Tools;
using SignalGrid.Ampelum.Tools.Calculator;
using Shouldly;
using Xunit;

namespace Ampelum.Tools.Calculator.Tests;

public sealed class CalculatorToolTests
{
    [Fact]
    public async Task Calculator_Evaluates_PiTimesRSquared()
    {
        var tool = new CalculatorTool("3.14159 * 5 * 5");
        var result = await tool.InvokeAsync(TestContext.Current.CancellationToken);
        result.Value.ShouldBe(78.53975, 1e-6);
        result.Expression.ShouldBe("3.14159 * 5 * 5");
    }

    [Fact]
    public async Task Calculator_HandlesIntegerArithmetic()
    {
        var tool = new CalculatorTool("(2 + 3) * 4");
        var r = await tool.InvokeAsync(TestContext.Current.CancellationToken);
        r.Value.ShouldBe(20.0);
    }

    [Fact]
    public void Calculator_HasToolCategoryCompute()
    {
        var attr = typeof(CalculatorTool).GetCustomAttribute<ToolCategoryAttribute>();
        attr.ShouldNotBeNull();
        attr!.Kind.ShouldBe(ToolCategoryKind.Compute);
    }

    [Fact]
    public void Calculator_NameAndDescription_AreNonEmpty()
    {
        var tool = new CalculatorTool("1+1");
        tool.Name.ShouldBe("calculator");
        tool.Description.ShouldNotBeNullOrWhiteSpace();
        tool.Expression.ShouldBe("1+1");
    }

    [Fact]
    public async Task Calculator_HonoursCancellation()
    {
        var tool = new CalculatorTool("1+1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await tool.InvokeAsync(cts.Token));
    }
}
