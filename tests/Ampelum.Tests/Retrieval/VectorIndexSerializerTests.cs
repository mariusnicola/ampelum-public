using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Persistence;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class VectorIndexSerializerTests
{
    [Fact]
    public async Task RoundTrip_InMemoryStore_PreservesData()
    {
        var store = new InMemoryVectorStore();
        var docs = new[] { new RetrievedDocument("a", "alpha", 0, new Dictionary<string, object?> { ["k"] = "v" }) };
        var vectors = new[] { new ReadOnlyMemory<float>([0.1f, 0.2f, 0.3f]) };
        await store.UpsertAsync(docs, vectors, TestContext.Current.CancellationToken);

        using var ms = new MemoryStream();
        await VectorIndexSerializer.SaveAsync(store, ms, TestContext.Current.CancellationToken);
        ms.Position = 0;
        var restored = new InMemoryVectorStore();
        await VectorIndexSerializer.LoadAsync(restored, ms, TestContext.Current.CancellationToken);

        var qr = await restored.QueryAsync(new ReadOnlyMemory<float>([0.1f, 0.2f, 0.3f]), 1, ct: TestContext.Current.CancellationToken);
        qr.Single().Document.Id.ShouldBe("a");
        qr.Single().Document.Metadata["k"]!.ToString().ShouldBe("v");
    }

    static async Task<InMemoryVectorStore> Reloaded(IReadOnlyDictionary<string, object?> metadata)
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync(
            [new RetrievedDocument("a", "alpha", 0, metadata)],
            [new ReadOnlyMemory<float>([1f, 0f])],
            TestContext.Current.CancellationToken);
        using var ms = new MemoryStream();
        await VectorIndexSerializer.SaveAsync(store, ms, TestContext.Current.CancellationToken);
        ms.Position = 0;
        var restored = new InMemoryVectorStore();
        await VectorIndexSerializer.LoadAsync(restored, ms, TestContext.Current.CancellationToken);
        return restored;
    }

    [Fact]
    public async Task Load_HandsStringBoolAndNullBackPlain_AndANumberAsAJsonElement()
    {
        var restored = await Reloaded(new Dictionary<string, object?>
        {
            ["source"] = "history",
            ["year"] = 2024,
            ["big"] = 5_000_000_000L,
            ["ratio"] = 0.5,
            ["ok"] = true,
            ["off"] = false,
            ["none"] = null,
        });

        var hit = (await restored.QueryAsync(new ReadOnlyMemory<float>([1f, 0f]), 1, ct: TestContext.Current.CancellationToken)).Single();

        hit.Document.Metadata["source"].ShouldBe("history");
        hit.Document.Metadata["year"].ShouldBeOfType<System.Text.Json.JsonElement>().GetInt32().ShouldBe(2024);
        hit.Document.Metadata["big"].ShouldBeOfType<System.Text.Json.JsonElement>().GetInt64().ShouldBe(5_000_000_000L);
        hit.Document.Metadata["ratio"].ShouldBeOfType<System.Text.Json.JsonElement>().GetDouble().ShouldBe(0.5);
        hit.Document.Metadata["ok"].ShouldBe(true);
        hit.Document.Metadata["off"].ShouldBe(false);
        hit.Document.Metadata["none"].ShouldBeNull();
    }

    [Fact]
    public async Task Load_AStoreSideEqualityFilterMatchesTheReloadedDocument()
    {
        var restored = await Reloaded(new Dictionary<string, object?> { ["source"] = "history", ["year"] = 2024 });
        var query = new ReadOnlyMemory<float>([1f, 0f]);
        var ct = TestContext.Current.CancellationToken;

        (await restored.QueryAsync(query, 1, new Dictionary<string, object?> { ["source"] = "history" }, ct)).Count.ShouldBe(1);
        (await restored.QueryAsync(query, 1, new Dictionary<string, object?> { ["year"] = 2024 }, ct)).Count.ShouldBe(1);
        (await restored.QueryAsync(query, 1, new Dictionary<string, object?> { ["source"] = "style" }, ct)).Count.ShouldBe(0);
    }

    static async Task<byte[]> Saved(InMemoryVectorStore store)
    {
        using var ms = new MemoryStream();
        await VectorIndexSerializer.SaveAsync(store, ms, TestContext.Current.CancellationToken);
        return ms.ToArray();
    }

    static Dictionary<string, object?> NumberMetadata() => new()
    {
        ["ulongMax"] = ulong.MaxValue,
        ["dec"] = 1.10m,
        ["whole"] = 5.0,
        ["f"] = 0.1f,
        ["l"] = 5L,
        ["s"] = (short)7,
        ["small"] = 1E-10,
        ["text"] = "t",
        ["flag"] = true,
        ["off"] = false,
        ["none"] = null,
    };

    [Fact]
    public async Task SaveLoadSave_AnyNumberInMetadata_GivesIdenticalBytes()
    {
        var first = new InMemoryVectorStore();
        await first.UpsertAsync(
            [new RetrievedDocument("a", "alpha", 0, NumberMetadata())],
            [new ReadOnlyMemory<float>([1f, 0f])],
            TestContext.Current.CancellationToken);
        var bytes = await Saved(first);

        var restored = new InMemoryVectorStore();
        using (var ms = new MemoryStream(bytes))
            await VectorIndexSerializer.LoadAsync(restored, ms, TestContext.Current.CancellationToken);

        (await Saved(restored)).ShouldBe(bytes);
    }

    public static TheoryData<string, object> StoredValues => new()
    {
        { "l", 5L },
        { "whole", 5.0 },
        { "dec", 1.10m },
        { "f", 0.1f },
        { "ulongMax", ulong.MaxValue },
        { "s", (short)7 },
        { "small", 1E-10 },
        { "l", 5 },
        { "whole", 5 },
        { "dec", 1.1 },
        { "f", 0.1 },
        { "text", "t" },
        { "flag", true },
    };

    [Theory]
    [MemberData(nameof(StoredValues))]
    public async Task Load_AFilterWrittenWithTheStoredValue_MatchesAfterReload(string key, object wanted)
    {
        var restored = await Reloaded(NumberMetadata());

        var hits = await restored.QueryAsync(new ReadOnlyMemory<float>([1f, 0f]), 1,
            new Dictionary<string, object?> { [key] = wanted }, TestContext.Current.CancellationToken);

        hits.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("l", 6L)]
    [InlineData("whole", 5.5)]
    [InlineData("dec", 1.11)]
    [InlineData("ulongMax", 1.0E+300)]
    [InlineData("text", 5)]
    [InlineData("l", "5")]
    [InlineData("flag", 1)]
    [InlineData("none", 0)]
    public async Task Load_AFilterWithAnotherValue_DoesNotMatchAfterReload(string key, object wanted)
    {
        var restored = await Reloaded(NumberMetadata());

        var hits = await restored.QueryAsync(new ReadOnlyMemory<float>([1f, 0f]), 1,
            new Dictionary<string, object?> { [key] = wanted }, TestContext.Current.CancellationToken);

        hits.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Load_ANumberBeyondTheDecimalRange_StillMatchesItself_ComparedAsDouble()
    {
        var restored = await Reloaded(new Dictionary<string, object?> { ["huge"] = 1.0E+300 });

        var hits = await restored.QueryAsync(new ReadOnlyMemory<float>([1f, 0f]), 1,
            new Dictionary<string, object?> { ["huge"] = 1.0E+300 }, TestContext.Current.CancellationToken);

        hits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Load_AFloatFilterValue_DoesNotMatchTheSameNumberStoredAsADouble()
    {
        var restored = await Reloaded(new Dictionary<string, object?> { ["f"] = 0.1f });

        var hits = await restored.QueryAsync(new ReadOnlyMemory<float>([1f, 0f]), 1,
            new Dictionary<string, object?> { ["f"] = (double)0.1f }, TestContext.Current.CancellationToken);

        hits.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Load_AnArrayOrObjectInMetadata_StaysAJsonElement()
    {
        var restored = await Reloaded(new Dictionary<string, object?> { ["tags"] = new[] { "a", "b" } });

        var hit = (await restored.QueryAsync(new ReadOnlyMemory<float>([1f, 0f]), 1, ct: TestContext.Current.CancellationToken)).Single();

        hit.Document.Metadata["tags"].ShouldBeOfType<System.Text.Json.JsonElement>().GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task SaveLoad_TwoRounds_ProducesIdenticalBytes()
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync(
            [new RetrievedDocument("a", "x", 0, new Dictionary<string, object?>())],
            [new ReadOnlyMemory<float>([1f, 2f])],
            TestContext.Current.CancellationToken);
        using var first = new MemoryStream();
        await VectorIndexSerializer.SaveAsync(store, first, TestContext.Current.CancellationToken);
        var bytes = first.ToArray();

        var restored = new InMemoryVectorStore();
        using var seek = new MemoryStream(bytes);
        await VectorIndexSerializer.LoadAsync(restored, seek, TestContext.Current.CancellationToken);
        using var second = new MemoryStream();
        await VectorIndexSerializer.SaveAsync(restored, second, TestContext.Current.CancellationToken);
        second.ToArray().ShouldBe(bytes);
    }

    [Fact]
    public async Task Save_NonInMemoryStore_Throws()
    {
        var fake = new FakeStore();
        using var ms = new MemoryStream();
        await Should.ThrowAsync<NotSupportedException>(() => VectorIndexSerializer.SaveAsync(fake, ms, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Load_NonInMemoryStore_Throws()
    {
        var fake = new FakeStore();
        using var ms = new MemoryStream();
        await Should.ThrowAsync<NotSupportedException>(() => VectorIndexSerializer.LoadAsync(fake, ms, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Load_BadMagic_Throws()
    {
        using var ms = new MemoryStream(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });
        var store = new InMemoryVectorStore();
        await Should.ThrowAsync<InvalidDataException>(() => VectorIndexSerializer.LoadAsync(store, ms, TestContext.Current.CancellationToken));
    }

    private sealed class FakeStore : IVectorStore
    {
        public Task UpsertAsync(IReadOnlyList<RetrievedDocument> documents, ReadOnlyMemory<float>[] vectors, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<(RetrievedDocument Document, double Score)>> QueryAsync(ReadOnlyMemory<float> queryVector, int topK, IReadOnlyDictionary<string, object?>? filters = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(RetrievedDocument, double)>>([]);
    }
}
