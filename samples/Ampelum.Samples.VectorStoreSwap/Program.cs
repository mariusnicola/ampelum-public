using Ampelum.Samples.Shared;
using Ampelum.Samples.VectorStoreSwap;
using SignalGrid.Ampelum.Testing;

if (args.Contains("--fake"))
{
    // Each fake store receives the same calls as a real one would, backed by a ranking held in memory.
    var backends = new VectorBackends(new FakePgVectorBackend(), new FakePineconeIndex(), new FakeQdrantBackend());
    var chat = new FakeChatClient().EnqueueText("Standard delivery takes 3 to 5 business days [doc1].");
    var embeddings = VectorStoreSwapData.FakeEmbeddings();

    var pipelines = VectorStoreKinds.All
        .Select(kind => new VectorStoreSwapPipeline(FakeSettings.For(kind), embeddings, chat, backends))
        .ToList();
    try
    {
        return await SwapReport.RunAsync(pipelines, Console.Out, fakeBackends: true);
    }
    finally
    {
        foreach (var pipeline in pipelines)
            await pipeline.DisposeAsync();
    }
}

var config = SampleConfig.Load();
var kindName = config.Require(SettingNames.VectorStore);
var liveEmbeddings = LiveEmbeddings.Create(config.Require);
var liveChat = config.CreateChatClient();

// The in-memory store holds the same live embeddings, so it is the reference ranking for the chosen store.
var referenceSetting = (string key) => key == SettingNames.VectorStore ? VectorStoreKinds.InMemory : config.Require(key);
await using var chosen = new VectorStoreSwapPipeline(config.Require, liveEmbeddings, liveChat);
await using var reference = new VectorStoreSwapPipeline(referenceSetting, liveEmbeddings, liveChat);
Console.WriteLine($"Configured store: {kindName}");
return await SwapReport.RunAsync([chosen, reference], Console.Out);
