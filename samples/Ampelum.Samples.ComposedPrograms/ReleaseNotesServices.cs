using Ampelum.Samples.ComposedPrograms.Evaluation;
using Ampelum.Samples.ComposedPrograms.Observability;
using Ampelum.Samples.ComposedPrograms.Review;
using Ampelum.Samples.ComposedPrograms.Style;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agents.Memory;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Retrieval;

namespace Ampelum.Samples.ComposedPrograms;

/// <summary>The service registrations of the pipeline, in one place so the wiring can be read top to bottom.</summary>
public static class ReleaseNotesServices
{
    public static IServiceProvider Build(
        IChatClient chat, ReleaseNotesOptions options, ILoggerFactory loggerFactory,
        InMemoryTraceSink traces, TraceFileExporter exporter, IReadOnlyCollection<ChangeRow> changes,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(chat);
        services.AddSingleton(loggerFactory);
        services.AddSingleton(loggerFactory.CreateLogger<LoggingTraceSink>());
        services.AddSingleton(traces);
        services.AddSingleton(exporter);

        // One cache for embeddings, so a text that is embedded twice costs one embedding.
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var embedder = StyleMemory.Cached(embeddings ?? new HashingEmbeddingGenerator(), cache);
        services.AddSingleton(embedder);

        var builder = services.AddAmpelum().AddAgents();
        builder.AddReleaseMetrics(embedder, chat, options);

        if (options.QueuedTracing)
            builder.UseTraceSink<QueuedTraceSink>();
        else
            builder.UseSynchronousTraceSink<FanOutTraceSink>();
        services.AddSingleton<IHook>(new GenAiLogHook(loggerFactory.CreateLogger<GenAiLogHook>()));

        services.AddSingleton<CompositeConversationMemory>(
            _ => ReviewPanel.BuildMemory(chat, new InMemoryVectorStore(), embedder, options));
        builder.AddConversationMemory<CompositeConversationMemory>();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SignalGrid.Ampelum.Agents.Tools.IAgentToolRegistry>().Register(new ChangeCountsTool(changes));
        return provider;
    }
}
