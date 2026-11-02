using SignalGrid.Ampelum.Testing.Context;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Testing.Tests.Context;

public class FakeEmbedderTests
{
    [Fact]
    public async Task Embed_Returns384Dim()
    {
        var e = new FakeEmbedder();
        var v = await e.EmbedAsync("hello", TestContext.Current.CancellationToken);
        v.Length.ShouldBe(384);
    }

    [Fact]
    public async Task Embed_SameInput_ReturnsSameVector()
    {
        var e = new FakeEmbedder();
        var v1 = await e.EmbedAsync("test", TestContext.Current.CancellationToken);
        var v2 = await e.EmbedAsync("test", TestContext.Current.CancellationToken);
        v1.ToArray().ShouldBe(v2.ToArray());
    }
}
