using SignalGrid.Ampelum.Retrieval.Caching;
using Ampelum.Tests.Retrieval.Fixtures;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class EmbeddingCacheTests
{
    private static IDistributedCache NewCache()
        => new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    [Fact]
    public async Task IdenticalText_HitsCache_OnSecondCall()
    {
        var inner = new FakeEmbeddingGenerator();
        var sut = new EmbeddingCache(inner, NewCache(), new EmbeddingCacheOptions());

        var first = await sut.GenerateAsync(["hello"], cancellationToken: TestContext.Current.CancellationToken);
        var second = await sut.GenerateAsync(["hello"], cancellationToken: TestContext.Current.CancellationToken);

        inner.Calls.Count.ShouldBe(1);
        first[0].Vector.ToArray().ShouldBe(second[0].Vector.ToArray());
    }

    [Fact]
    public async Task DifferentText_MissesCache()
    {
        var inner = new FakeEmbeddingGenerator();
        var sut = new EmbeddingCache(inner, NewCache(), new EmbeddingCacheOptions());

        await sut.GenerateAsync(["a"], cancellationToken: TestContext.Current.CancellationToken);
        await sut.GenerateAsync(["b"], cancellationToken: TestContext.Current.CancellationToken);

        inner.Calls.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task BatchQuery_PartialHit_ForwardsOnlyMisses()
    {
        var inner = new FakeEmbeddingGenerator();
        var sut = new EmbeddingCache(inner, NewCache(), new EmbeddingCacheOptions());

        await sut.GenerateAsync(["a"], cancellationToken: TestContext.Current.CancellationToken);
        var results = await sut.GenerateAsync(["a", "b"], cancellationToken: TestContext.Current.CancellationToken);

        inner.Calls.ShouldBe(["a", "b"]);
        results.Count.ShouldBe(2);
    }

    [Fact]
    public async Task TimeToLive_AppliesAbsoluteExpiration()
    {
        var inner = new FakeEmbeddingGenerator();
        var opts = new EmbeddingCacheOptions { TimeToLive = TimeSpan.FromMinutes(10) };
        var sut = new EmbeddingCache(inner, NewCache(), opts);

        var r1 = await sut.GenerateAsync(["x"], cancellationToken: TestContext.Current.CancellationToken);
        var r2 = await sut.GenerateAsync(["x"], cancellationToken: TestContext.Current.CancellationToken);

        inner.Calls.Count.ShouldBe(1);
        r1[0].Vector.ToArray().ShouldBe(r2[0].Vector.ToArray());
    }

    [Fact]
    public void Metadata_DelegatesToInner()
    {
        var inner = new FakeEmbeddingGenerator();
        var sut = new EmbeddingCache(inner, NewCache(), new EmbeddingCacheOptions());
        sut.Metadata.ShouldBe(inner.Metadata);
    }

    [Fact]
    public void GetService_ReturnsSelf_ForOwnType()
    {
        var inner = new FakeEmbeddingGenerator();
        var sut = new EmbeddingCache(inner, NewCache(), new EmbeddingCacheOptions());
        sut.GetService(typeof(EmbeddingCache)).ShouldBe(sut);
    }

    [Fact]
    public void GetService_FallsBackToInner_ForOtherType()
    {
        var inner = new FakeEmbeddingGenerator();
        var sut = new EmbeddingCache(inner, NewCache(), new EmbeddingCacheOptions());
        sut.GetService(typeof(FakeEmbeddingGenerator)).ShouldBe(inner);
        sut.GetService(typeof(string)).ShouldBeNull();
    }

    [Fact]
    public void Dispose_DisposesInner()
    {
        var sut = new EmbeddingCache(new FakeEmbeddingGenerator(), NewCache(), new EmbeddingCacheOptions());
        Should.NotThrow(() => sut.Dispose());
    }
}
