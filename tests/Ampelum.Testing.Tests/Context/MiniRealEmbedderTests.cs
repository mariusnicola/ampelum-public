using SignalGrid.Ampelum.Testing.Context;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Testing.Tests.Context;

public class MiniRealEmbedderTests
{
    [Fact]
    public async Task Embed_Returns64Dim()
    {
        var e = new MiniRealEmbedder();
        var v = await e.EmbedAsync("hello", TestContext.Current.CancellationToken);
        v.Length.ShouldBe(64);
    }

    [Fact]
    public async Task SimilarText_HigherCosineThanDissimilar()
    {
        var e = new MiniRealEmbedder();
        var vA = await e.EmbedAsync("read file content", TestContext.Current.CancellationToken);
        var vB = await e.EmbedAsync("read file", TestContext.Current.CancellationToken);
        var vC = await e.EmbedAsync("network packet protocol", TestContext.Current.CancellationToken);
        var a = vA.ToArray(); var b = vB.ToArray(); var c = vC.ToArray();
        var cosAB = Cosine(a, b);
        var cosAC = Cosine(a, c);
        cosAB.ShouldBeGreaterThan(cosAC);
    }

    private static float Cosine(float[] a, float[] b)
    {
        var dot = a.Zip(b, (x, y) => x * y).Sum();
        var na = MathF.Sqrt(a.Sum(x => x * x));
        var nb = MathF.Sqrt(b.Sum(x => x * x));
        return na * nb > 0 ? dot / (na * nb) : 0;
    }
}
