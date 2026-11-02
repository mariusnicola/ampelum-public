using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Batch;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Batch;

[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class BatchTypesTests
{
    [Fact]
    public void BatchHandle_RecordEquality()
    {
        var ts = DateTimeOffset.UtcNow;
        var a = new BatchHandle("id", "openai", ts);
        var b = new BatchHandle("id", "openai", ts);
        a.ShouldBe(b);
        a.ToString().ShouldContain("id");
    }

    [Fact]
    public void BatchRequest_DefaultsOptionsToNull()
    {
        var r = new BatchRequest("x", new[] { new ChatMessage(ChatRole.User, "hi") });
        r.Options.ShouldBeNull();
    }

    [Fact]
    public void BatchStatus_RecordEquality()
    {
        var a = new BatchStatus(BatchState.Completed, 1, 2, 3);
        var b = new BatchStatus(BatchState.Completed, 1, 2, 3);
        a.ShouldBe(b);
    }

    [Fact]
    public void BatchResultItem_RecordEquality()
    {
        var a = new BatchResultItem("x", null, "err");
        var b = new BatchResultItem("x", null, "err");
        a.ShouldBe(b);
    }

    [Theory]
    [InlineData(BatchState.InProgress)]
    [InlineData(BatchState.Completed)]
    [InlineData(BatchState.Failed)]
    [InlineData(BatchState.Cancelled)]
    [InlineData(BatchState.Expired)]
    public void BatchState_AllValuesRoundTrip(BatchState state)
    {
        state.ShouldBe(state);
    }
}
