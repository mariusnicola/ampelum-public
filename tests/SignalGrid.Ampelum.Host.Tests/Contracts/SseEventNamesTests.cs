#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Contracts;

public sealed class SseEventNamesTests
{
    [Fact]
    public void Token_IsCorrectString()
    {
        SseEventNames.Token.ShouldBe("token");
    }

    [Fact]
    public void Partial_IsCorrectString()
    {
        SseEventNames.Partial.ShouldBe("partial");
    }

    [Fact]
    public void Final_IsCorrectString()
    {
        SseEventNames.Final.ShouldBe("final");
    }

    [Fact]
    public void Error_IsCorrectString()
    {
        SseEventNames.Error.ShouldBe("error");
    }

    [Fact]
    public void Done_IsCorrectString()
    {
        SseEventNames.Done.ShouldBe("done");
    }

    [Fact]
    public void Progress_IsCorrectString()
    {
        SseEventNames.Progress.ShouldBe("progress");
    }

    [Fact]
    public void Trial_IsCorrectString()
    {
        SseEventNames.Trial.ShouldBe("trial");
    }

    [Fact]
    public void Forward_All_ContainsExpectedEvents()
    {
        SseEventNames.Forward.All.ShouldBe(
            new[] { SseEventNames.Token, SseEventNames.Partial, SseEventNames.Final, SseEventNames.Error, SseEventNames.Done });
    }

    [Fact]
    public void Optimize_All_ContainsExpectedEvents()
    {
        SseEventNames.Optimize.All.ShouldBe(
            new[] { SseEventNames.Progress, SseEventNames.Trial, SseEventNames.Final, SseEventNames.Error, SseEventNames.Done });
    }

    [Fact]
    public void Final_Error_Done_AreSharedByReference()
    {
        SseEventNames.Forward.All.ShouldContain(SseEventNames.Final);
        SseEventNames.Optimize.All.ShouldContain(SseEventNames.Final);
        ReferenceEquals(SseEventNames.Forward.All[2], SseEventNames.Optimize.All[2]).ShouldBeTrue();

        SseEventNames.Forward.All.ShouldContain(SseEventNames.Error);
        SseEventNames.Optimize.All.ShouldContain(SseEventNames.Error);
        ReferenceEquals(SseEventNames.Forward.All[3], SseEventNames.Optimize.All[3]).ShouldBeTrue();

        SseEventNames.Forward.All.ShouldContain(SseEventNames.Done);
        SseEventNames.Optimize.All.ShouldContain(SseEventNames.Done);
        ReferenceEquals(SseEventNames.Forward.All[4], SseEventNames.Optimize.All[4]).ShouldBeTrue();
    }
}
