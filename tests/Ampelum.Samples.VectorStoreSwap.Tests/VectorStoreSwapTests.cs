using Microsoft.Extensions.AI;
using Shouldly;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Samples.VectorStoreSwap.Tests;

public sealed class VectorStoreSwapTests
{
    const string Question = VectorStoreSwapData.SampleQuestion;
    const string Billing = "billing";

    sealed record Fakes(FakePgVectorBackend Pg, FakePineconeIndex Pinecone, FakeQdrantBackend Qdrant)
    {
        public static Fakes Create() => new(new FakePgVectorBackend(), new FakePineconeIndex(), new FakeQdrantBackend());

        public VectorBackends Backends => new(Pg, Pinecone, Qdrant);
    }

    static VectorStoreSwapPipeline Build(
        string kind,
        Fakes fakes,
        FakeChatClient? chat = null,
        Func<string, string>? setting = null,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings = null) =>
        new(setting ?? FakeSettings.For(kind),
            embeddings ?? VectorStoreSwapData.FakeEmbeddings(),
            chat ?? new FakeChatClient(),
            fakes.Backends);

    static async Task<VectorStoreSwapPipeline> Indexed(
        string kind, Fakes fakes, FakeChatClient? chat = null, IReadOnlyList<RetrievedDocument>? documents = null)
    {
        var pipeline = Build(kind, fakes, chat);
        await pipeline.IndexAsync(documents ?? VectorStoreSwapData.Documents, TestContext.Current.CancellationToken);
        return pipeline;
    }

    static async Task<List<VectorStoreSwapPipeline>> IndexedAll(Fakes fakes)
    {
        var all = new List<VectorStoreSwapPipeline>();
        foreach (var kind in VectorStoreKinds.All)
            all.Add(await Indexed(kind, fakes));
        return all;
    }

    static Func<string, string> Override(Func<string, string> inner, string key, string value) =>
        k => k == key ? value : inner(k);

    static IReadOnlyDictionary<string, object?> Category(string value) =>
        new Dictionary<string, object?> { [VectorStoreSwapData.CategoryKey] = value };

    [Theory]
    [InlineData(VectorStoreKinds.PgVector)]
    [InlineData(VectorStoreKinds.Pinecone)]
    [InlineData(VectorStoreKinds.Qdrant)]
    [InlineData(VectorStoreKinds.InMemory)]
    public async Task Setting_SelectsTheStoreThatReceivesTheCalls(string kind)
    {
        var fakes = Fakes.Create();
        await using var pipeline = await Indexed(kind, fakes);

        var result = await pipeline.RetrieveAsync(Question, 3, ct: TestContext.Current.CancellationToken);

        result.Store.ShouldBe(kind);
        result.Hits.ShouldNotBeEmpty();
        (fakes.Pg.Requests.Count > 0).ShouldBe(kind == VectorStoreKinds.PgVector);
        (fakes.Pinecone.Requests.Count > 0).ShouldBe(kind == VectorStoreKinds.Pinecone);
        (fakes.Qdrant.Requests.Count > 0).ShouldBe(kind == VectorStoreKinds.Qdrant);
    }

    [Fact]
    public void UnknownStore_IsRejectedAndListsTheValidOnes()
    {
        var ex = Should.Throw<InvalidOperationException>(() => Build("weaviate", Fakes.Create()));

        ex.Message.ShouldContain("Unknown VECTOR_STORE 'weaviate'");
        ex.Message.ShouldContain("pgvector, pinecone, qdrant, inmemory");
    }

    [Fact]
    public void MissingSettingOfTheChosenStore_FailsNamingTheKey()
    {
        var settings = FakeSettings.For(VectorStoreKinds.Qdrant);
        Func<string, string> withoutCollection = k => k == SettingNames.QdrantCollection
            ? throw new InvalidOperationException($"Missing setting {k}.")
            : settings(k);

        var ex = Should.Throw<InvalidOperationException>(() => Build(VectorStoreKinds.Qdrant, Fakes.Create(), setting: withoutCollection));

        ex.Message.ShouldContain(SettingNames.QdrantCollection);
    }

    static readonly Dictionary<string, string[]> SettingsOfStore = new()
    {
        [VectorStoreKinds.PgVector] = [SettingNames.PgVectorConnectionString, SettingNames.VectorDimension],
        [VectorStoreKinds.Pinecone] = [SettingNames.PineconeApiKey, SettingNames.PineconeEnvironment, SettingNames.PineconeIndex],
        [VectorStoreKinds.Qdrant] = [SettingNames.QdrantHost, SettingNames.QdrantPort, SettingNames.QdrantCollection, SettingNames.VectorDimension],
        [VectorStoreKinds.InMemory] = [],
    };

    [Theory]
    [InlineData(VectorStoreKinds.PgVector)]
    [InlineData(VectorStoreKinds.Pinecone)]
    [InlineData(VectorStoreKinds.Qdrant)]
    [InlineData(VectorStoreKinds.InMemory)]
    public async Task ChosenStore_NeedsOnlyItsOwnSettings(string kind)
    {
        var all = FakeSettings.For(kind);
        var allowed = SettingsOfStore[kind].Append(SettingNames.VectorStore).ToHashSet();
        Func<string, string> onlyOwn = k => allowed.Contains(k)
            ? all(k)
            : throw new InvalidOperationException($"Missing setting {k}.");

        await using var pipeline = Build(kind, Fakes.Create(), setting: onlyOwn);
        await pipeline.IndexAsync(VectorStoreSwapData.Documents, TestContext.Current.CancellationToken);

        (await pipeline.RetrieveAsync(Question, 3, ct: TestContext.Current.CancellationToken)).Hits.ShouldNotBeEmpty();
    }

    static Func<string, string> DistinctSettings() => key => key switch
    {
        SettingNames.VectorDimension => "42",
        SettingNames.PgVectorConnectionString => "Host=pg.example.test;Database=db-x",
        SettingNames.PineconeApiKey => "pinecone-key-x",
        SettingNames.PineconeEnvironment => "pinecone-env-x",
        SettingNames.PineconeIndex => "pinecone-index-x",
        SettingNames.QdrantHost => "qdrant.example.test",
        SettingNames.QdrantPort => "7777",
        SettingNames.QdrantCollection => "qdrant-collection-x",
        _ => throw new InvalidOperationException($"Missing setting {key}."),
    };

    [Fact]
    public void PgVectorOptions_AreBuiltFromEverySetting()
    {
        var options = VectorStoreRegistration.PgVectorOptionsFrom(DistinctSettings());

        options.ConnectionString.ShouldBe("Host=pg.example.test;Database=db-x");
        options.VectorDimension.ShouldBe(42);
    }

    [Fact]
    public void PineconeOptions_AreBuiltFromEverySetting()
    {
        var options = VectorStoreRegistration.PineconeOptionsFrom(DistinctSettings());

        options.ApiKey.ShouldBe("pinecone-key-x");
        options.Environment.ShouldBe("pinecone-env-x");
        options.IndexName.ShouldBe("pinecone-index-x");
    }

    [Fact]
    public void QdrantOptions_AreBuiltFromEverySetting()
    {
        var options = VectorStoreRegistration.QdrantOptionsFrom(DistinctSettings());

        options.Host.ShouldBe("qdrant.example.test");
        options.Port.ShouldBe(7777);
        options.CollectionName.ShouldBe("qdrant-collection-x");
        options.VectorDimension.ShouldBe(42);
    }

    [Fact]
    public async Task Qdrant_DoesNotReturnTheOriginalIdOfANonUuidDocument()
    {
        var plain = new RetrievedDocument("ticket-1", VectorStoreSwapData.Documents[0].Text, 0, Category("logistics"));
        var braced = new RetrievedDocument(
            "{" + VectorStoreSwapData.RefundsId.ToUpperInvariant() + "}", VectorStoreSwapData.Documents[1].Text, 0, Category(Billing));
        var documents = new[] { plain, braced };

        foreach (var kind in new[] { VectorStoreKinds.PgVector, VectorStoreKinds.Qdrant })
        {
            await using var pipeline = await Indexed(kind, Fakes.Create(), documents: documents);

            var ids = (await pipeline.RetrieveAsync(Question, 2, ct: TestContext.Current.CancellationToken)).Ids;

            if (kind == VectorStoreKinds.PgVector)
            {
                ids.ShouldBe([plain.Id, braced.Id], kind);
                continue;
            }
            ids.ShouldNotContain(plain.Id);
            ids.ShouldNotContain(braced.Id);
            ids.ShouldContain(FakeQdrantBackend.ToPointUuid(plain.Id));
            // A UUID in another notation comes back in the canonical lowercase form.
            ids.ShouldContain(VectorStoreSwapData.RefundsId);
            Guid.TryParse(FakeQdrantBackend.ToPointUuid(plain.Id), out _).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Scores_AreNotCompared_WhenFewerThanTwoServiceStoresRan()
    {
        var fakes = Fakes.Create();
        await using var service = await Indexed(VectorStoreKinds.Qdrant, fakes, new FakeChatClient().EnqueueText("answer"));
        await using var reference = await Indexed(VectorStoreKinds.InMemory, fakes);

        var report = await StoreComparison.RunAsync([service, reference], Question, 3, ct: TestContext.Current.CancellationToken);
        var output = new StringWriter();
        await SwapReport.RunAsync([service, reference], output);

        report.ScoresMatch.ShouldBeNull();
        output.ToString().ShouldContain("not compared");
        output.ToString().ShouldNotContain("Same scores");
    }

    [Fact]
    public async Task FakeRun_SaysTheThreeServiceBackendsAreOneInMemoryFake()
    {
        var fakes = Fakes.Create();
        var all = await IndexedAll(fakes);
        // Each report asks the first pipeline once.
        var chat = new FakeChatClient().EnqueueText("answer").EnqueueText("answer");
        all[0] = await Indexed(VectorStoreKinds.PgVector, fakes, chat);
        var fakeOutput = new StringWriter();
        var liveOutput = new StringWriter();

        var exit = await SwapReport.RunAsync(all, fakeOutput, fakeBackends: true);
        await SwapReport.RunAsync(all, liveOutput);

        exit.ShouldBe(0);
        fakeOutput.ToString().ShouldContain("one in-memory fake");
        liveOutput.ToString().ShouldNotContain("one in-memory fake");
        await DisposeAll(all);
    }

    [Fact]
    public async Task PgVector_ReceivesSchemaRowsAndQueryBuiltFromTheSettings()
    {
        var fakes = Fakes.Create();
        var settings = Override(FakeSettings.For(VectorStoreKinds.PgVector), SettingNames.VectorDimension, "5");
        await using var pipeline = Build(VectorStoreKinds.PgVector, fakes, setting: settings);
        await pipeline.IndexAsync(VectorStoreSwapData.Documents, TestContext.Current.CancellationToken);

        await pipeline.RetrieveAsync(Question, 2, Category(Billing), TestContext.Current.CancellationToken);

        var requests = fakes.Pg.Requests;
        requests.Select(r => r.Operation).ShouldBe(["ensure-schema", "upsert", "query"]);
        requests[0].Dimension.ShouldBe(5);
        requests[0].Target.ShouldBe("ampelum_documents");
        var shipping = requests[1].Rows!.Single(r => r.Id == VectorStoreSwapData.ShippingId);
        shipping.Fields["metadata"].ShouldBe("""{"category":"logistics"}""");
        shipping.Fields["text"].ShouldStartWith("Shipping:");
        requests[2].TopK.ShouldBe(2);
        requests[2].Filter.ShouldNotBeNull().ShouldBe(new Dictionary<string, string> { ["category"] = Billing });
    }

    [Fact]
    public async Task PgVector_DimensionComesFromTheSetting()
    {
        var fakes = Fakes.Create();
        var settings = Override(FakeSettings.For(VectorStoreKinds.PgVector), SettingNames.VectorDimension, "7");
        await using var pipeline = Build(VectorStoreKinds.PgVector, fakes, setting: settings);

        await pipeline.IndexAsync(VectorStoreSwapData.Documents, TestContext.Current.CancellationToken);

        fakes.Pg.Requests[0].Dimension.ShouldBe(7);
    }

    [Fact]
    public async Task Pinecone_ReceivesTextInMetadataAndStringFilters()
    {
        var fakes = Fakes.Create();
        await using var pipeline = await Indexed(VectorStoreKinds.Pinecone, fakes);

        await pipeline.RetrieveAsync(Question, 2, Category(Billing), TestContext.Current.CancellationToken);

        var requests = fakes.Pinecone.Requests;
        requests.Select(r => r.Operation).ShouldBe(["upsert", "query"]);
        requests[0].Target.ShouldBe("");
        var shipping = requests[0].Rows!.Single(r => r.Id == VectorStoreSwapData.ShippingId);
        shipping.Fields["__text"].ShouldStartWith("Shipping:");
        shipping.Fields["category"].ShouldBe("logistics");
        requests[1].TopK.ShouldBe(2);
        requests[1].Filter.ShouldNotBeNull().ShouldBe(new Dictionary<string, string> { ["category"] = Billing });
    }

    [Fact]
    public async Task Qdrant_ReceivesCollectionDimensionPayloadAndFilter()
    {
        var fakes = Fakes.Create();
        await using var pipeline = await Indexed(VectorStoreKinds.Qdrant, fakes);

        await pipeline.RetrieveAsync(Question, 2, Category(Billing), TestContext.Current.CancellationToken);

        var requests = fakes.Qdrant.Requests;
        requests.Select(r => r.Operation).ShouldBe(["ensure-collection", "upsert", "search"]);
        requests[0].Target.ShouldBe("fake-collection");
        requests[0].Dimension.ShouldBe(VectorStoreSwapData.FakeDimension);
        var shipping = requests[1].Rows!.Single(r => r.Id == VectorStoreSwapData.ShippingId);
        shipping.Fields["__text"].ShouldStartWith("Shipping:");
        shipping.Fields["category"].ShouldBe("logistics");
        requests[2].Target.ShouldBe("fake-collection");
        requests[2].Filter.ShouldNotBeNull().ShouldBe(new Dictionary<string, string> { ["category"] = Billing });
    }

    [Fact]
    public async Task EveryStore_ReturnsTheSameRankingAndScoresForTheSameQuestion()
    {
        var pipelines = await IndexedAll(Fakes.Create());

        var report = await StoreComparison.RunAsync(pipelines, Question, 3, ct: TestContext.Current.CancellationToken);

        string[] expected = [VectorStoreSwapData.ShippingId, VectorStoreSwapData.RefundsId, VectorStoreSwapData.InvoicesId];
        foreach (var result in report.Results)
            result.Ids.ShouldBe(expected, $"store {result.Store}");
        report.RankingsMatch.ShouldBeTrue();
        report.ScoresMatch.ShouldBe(true);
        // Cosine similarity of the scripted vectors, worked out by hand.
        var pgScores = report.Results.Single(r => r.Store == VectorStoreKinds.PgVector).Hits.Select(h => h.Score).ToArray();
        pgScores[0].ShouldBe(0.9976, 0.001);
        pgScores[1].ShouldBe(0.5489, 0.001);
        pgScores[2].ShouldBe(0.3664, 0.001);
        await DisposeAll(pipelines);
    }

    [Fact]
    public async Task EveryStore_ReturnsTheSameTextAndMetadata()
    {
        var pipelines = await IndexedAll(Fakes.Create());

        var report = await StoreComparison.RunAsync(pipelines, Question, 3, ct: TestContext.Current.CancellationToken);

        // PgVector reads metadata back from JSON, so values are compared as text.
        string Shape(RetrievedDocument d) =>
            $"{d.Id}|{d.Text}|{string.Join(",", d.Metadata.Select(m => $"{m.Key}={m.Value}"))}";
        var reference = report.Results[0].Hits.Select(Shape).ToArray();
        foreach (var result in report.Results)
            result.Hits.Select(Shape).ToArray().ShouldBe(reference, $"store {result.Store}");
        reference[0].ShouldContain("category=logistics");
        await DisposeAll(pipelines);
    }

    [Theory]
    [InlineData(Billing, new[] { VectorStoreSwapData.RefundsId, VectorStoreSwapData.InvoicesId })]
    [InlineData("logistics", new[] { VectorStoreSwapData.ShippingId })]
    [InlineData("none-such", new string[0])]
    public async Task EveryStore_AppliesTheSameMetadataFilter(string category, string[] expected)
    {
        var pipelines = await IndexedAll(Fakes.Create());

        var report = await StoreComparison.RunAsync(pipelines, Question, 3, Category(category), TestContext.Current.CancellationToken);

        foreach (var result in report.Results)
            result.Ids.ShouldBe(expected, $"store {result.Store}");
        await DisposeAll(pipelines);
    }

    [Fact]
    public async Task RagAdvisor_CitesAndPromptsTheSameDocumentsForEveryStore()
    {
        foreach (var kind in VectorStoreKinds.All)
        {
            var chat = new FakeChatClient().EnqueueText("Delivery takes 3 to 5 business days [doc1].");
            await using var pipeline = await Indexed(kind, Fakes.Create(), chat);

            var answer = await pipeline.AskAsync(Question, 2, TestContext.Current.CancellationToken);

            answer.Answer.ShouldBe("Delivery takes 3 to 5 business days [doc1].");
            answer.Citations.Select(d => d.Id).ShouldBe([VectorStoreSwapData.ShippingId, VectorStoreSwapData.RefundsId], kind);
            var prompt = string.Join("\n", chat.RecordedCalls.Single().Messages.Select(m => m.Text));
            prompt.ShouldContain($"[doc1 id={VectorStoreSwapData.ShippingId}] Shipping:", customMessage: kind);
            prompt.ShouldContain($"Question: {Question}", customMessage: kind);
        }
    }

    [Fact]
    public async Task Comparison_ReportsAStoreThatReturnsADifferentRanking()
    {
        var fakes = Fakes.Create();
        await using var complete = await Indexed(VectorStoreKinds.PgVector, fakes);
        var withoutShipping = VectorStoreSwapData.Documents.Where(d => d.Id != VectorStoreSwapData.ShippingId).ToArray();
        await using var partial = await Indexed(VectorStoreKinds.Qdrant, fakes, documents: withoutShipping);

        var report = await StoreComparison.RunAsync([complete, partial], Question, 3, ct: TestContext.Current.CancellationToken);

        report.RankingsMatch.ShouldBeFalse();
    }

    [Fact]
    public async Task Comparison_ReportsAdaptersThatScoreTheSameRankingDifferently()
    {
        var fakes = Fakes.Create();
        await using var reference = await Indexed(VectorStoreKinds.PgVector, fakes);
        // A query vector tilted toward money keeps the order but changes every similarity.
        var tilted = VectorStoreSwapData.Documents.ToDictionary(d => d.Text, d => ScriptedVector(d.Id));
        tilted[Question] = [0.95f, 0.35f, 0.05f];
        await using var other = Build(
            VectorStoreKinds.Qdrant, fakes, embeddings: new FakeEmbeddingGenerator(tilted, VectorStoreSwapData.FakeDimension));
        await other.IndexAsync(VectorStoreSwapData.Documents, TestContext.Current.CancellationToken);

        var report = await StoreComparison.RunAsync([reference, other], Question, 3, ct: TestContext.Current.CancellationToken);

        report.RankingsMatch.ShouldBeTrue();
        report.ScoresMatch.ShouldBe(false);
    }

    static float[] ScriptedVector(string id) => id switch
    {
        VectorStoreSwapData.ShippingId => [0.90f, 0.20f, 0.10f],
        VectorStoreSwapData.RefundsId => [0.30f, 0.90f, 0.10f],
        VectorStoreSwapData.InvoicesId => [0.10f, 0.80f, 0.30f],
        _ => [0.10f, 0.10f, 0.95f],
    };

    static async Task DisposeAll(IEnumerable<VectorStoreSwapPipeline> pipelines)
    {
        foreach (var pipeline in pipelines)
            await pipeline.DisposeAsync();
    }
}
