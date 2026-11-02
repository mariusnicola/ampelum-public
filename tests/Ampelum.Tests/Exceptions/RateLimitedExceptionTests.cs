using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Exceptions;
using Shouldly;
using Xunit;

[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class RateLimitedExceptionTests
{
    [Fact]
    public void Ctor_StoresPropertiesAndMessage()
    {
        var ex = new RateLimitedException("tenant-A", TimeSpan.FromMilliseconds(500));
        ex.PartitionKey.ShouldBe("tenant-A");
        ex.WaitTime.ShouldBe(TimeSpan.FromMilliseconds(500));
        ex.Message.ShouldContain("tenant-A");
        ex.Message.ShouldContain("500");
    }
}
