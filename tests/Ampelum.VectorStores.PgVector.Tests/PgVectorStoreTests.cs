using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.VectorStores.PgVector;
using Shouldly;
using Xunit;

namespace Ampelum.VectorStores.PgVector.Tests;

public sealed class PgVectorStoreTests
{
    private sealed class FakeBackend : IPgVectorBackend
    {
        public List<(string Id, string Text, string MetaJson, ReadOnlyMemory<float> Embedding)> Stored { get; } = new();
        public string? LastTable;
        public int? LastDim;
        public IReadOnlyDictionary<string, string>? LastFilter;
        public bool Disposed;

        public Task EnsureSchemaAsync(string table, int dim, CancellationToken ct)
        {
            LastTable = table;
            LastDim = dim;
            return Task.CompletedTask;
        }

        public Task UpsertAsync(string table, IReadOnlyList<(string Id, string Text, string MetadataJson, ReadOnlyMemory<float> Embedding)> rows, CancellationToken ct)
        {
            foreach (var r in rows) Stored.Add((r.Id, r.Text, r.MetadataJson, r.Embedding));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<(string Id, string Text, string MetadataJson, double Score)>> QueryAsync(string table, ReadOnlyMemory<float> queryVector, int topK, IReadOnlyDictionary<string, string>? filterEquals, CancellationToken ct)
        {
            LastFilter = filterEquals;
            var hits = Stored.Take(topK).Select((p, i) => (p.Id, p.Text, p.MetaJson, (double)(1.0 - i * 0.1))).ToList();
            return Task.FromResult<IReadOnlyList<(string, string, string, double)>>(hits);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task UpsertQuery_RoundTrip()
    {
        var backend = new FakeBackend();
        await using var sut = new PgVectorStore(
            new PgVectorOptions { ConnectionString = "Host=ignored", VectorDimension = 3 },
            backend);
        await sut.UpsertAsync(
            [new RetrievedDocument("a", "alpha", 0, new Dictionary<string, object?> { ["k"] = "v" })],
            [new ReadOnlyMemory<float>([1f, 0f, 0f])],
            TestContext.Current.CancellationToken);

        var r = await sut.QueryAsync(new ReadOnlyMemory<float>([1f, 0f, 0f]), 1, ct: TestContext.Current.CancellationToken);
        r.Single().Document.Id.ShouldBe("a");
        r.Single().Document.Text.ShouldBe("alpha");
        backend.LastTable.ShouldBe("ampelum_documents");
        backend.LastDim.ShouldBe(3);
    }

    [Fact]
    public async Task Upsert_LengthMismatch_Throws()
    {
        await using var sut = new PgVectorStore(
            new PgVectorOptions { ConnectionString = "Host=ignored", VectorDimension = 3 },
            new FakeBackend());
        await Should.ThrowAsync<ArgumentException>(() => sut.UpsertAsync(
            [new RetrievedDocument("a", "t", 0, new Dictionary<string, object?>())],
            new ReadOnlyMemory<float>[2],
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Query_PassesFiltersToBackend()
    {
        var backend = new FakeBackend();
        await using var sut = new PgVectorStore(
            new PgVectorOptions { ConnectionString = "Host=x", VectorDimension = 3 }, backend);
        await sut.UpsertAsync(
            [new RetrievedDocument("a", "x", 0, new Dictionary<string, object?>())],
            [new ReadOnlyMemory<float>([1f, 0f, 0f])],
            TestContext.Current.CancellationToken);
        await sut.QueryAsync(new ReadOnlyMemory<float>([1f, 0f, 0f]), 1,
            filters: new Dictionary<string, object?> { ["lang"] = "en" },
            ct: TestContext.Current.CancellationToken);
        backend.LastFilter!["lang"].ShouldBe("en");
    }

    [Fact]
    public async Task DisposeAsync_DisposesBackend()
    {
        var backend = new FakeBackend();
        var sut = new PgVectorStore(new PgVectorOptions { ConnectionString = "x", VectorDimension = 3 }, backend);
        await sut.DisposeAsync();
        backend.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void Options_DefaultsAndSchema()
    {
        var o = new PgVectorOptions { ConnectionString = "x", VectorDimension = 4 };
        o.TableName.ShouldBe("ampelum_documents");
        var ddl = PgVectorSchema.CreateTable("t", 4);
        ddl.ShouldContain("vector(4)");
        ddl.ShouldContain("ivfflat");
    }
}
