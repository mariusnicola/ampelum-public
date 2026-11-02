#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Contracts;

public sealed class PromptStatusTests
{
    [Fact]
    public void Active_IsCorrectString()
    {
        PromptStatus.Active.ShouldBe("active");
    }

    [Fact]
    public void Challenger_IsCorrectString()
    {
        PromptStatus.Challenger.ShouldBe("challenger");
    }

    [Fact]
    public void Retired_IsCorrectString()
    {
        PromptStatus.Retired.ShouldBe("retired");
    }

    [Fact]
    public void All_ContainsAllValuesInOrder()
    {
        PromptStatus.All.ShouldBe(new[] { PromptStatus.Active, PromptStatus.Challenger, PromptStatus.Retired });
    }
}
