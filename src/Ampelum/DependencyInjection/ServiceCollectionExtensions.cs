using SignalGrid.Ampelum.Usage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace SignalGrid.Ampelum.DependencyInjection;

/// <summary>Extension methods for registering Ampelum services in an <see cref="IServiceCollection"/>.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core Ampelum services (adapter, trace sink, context, modules, usage tracker) with sensible defaults.
    /// Returns a <see cref="AmpelumBuilder"/> for further customisation.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>A <see cref="AmpelumBuilder"/> for chaining adapter and sink overrides.</returns>
    public static AmpelumBuilder AddAmpelum(this IServiceCollection services)
    {
        services.TryAddSingleton<IAdapter, ChatAdapter>();
        // v0.7 default-on batched trace sink (PSQ-3 / spec Â§H4): the default trace sink is a
        // BatchedTraceSink wrapping NullTraceSink, so trace-sink latency on the forward path is
        // bounded regardless of how slow a user-supplied inner sink is. UseTraceSink<T> replaces
        // the wrapper outright (sync semantics); UseBatchedTraceSinkInner<T> keeps the wrapper and
        // swaps the inner. Audit-sink batching is opt-in via UseAuditChain (Task 7).
        services.TryAddScoped<ITraceSink>(_ => new BatchedTraceSink(NullTraceSink.Instance));
        services.TryAddScoped<IUsageTracker, DefaultUsageTracker>();
        services.TryAddScoped<AmpelumContext>(sp =>
        {
            var chat = sp.GetRequiredService<Microsoft.Extensions.AI.IChatClient>();
            var adapter = sp.GetRequiredService<IAdapter>();
            var sink = sp.GetRequiredService<ITraceSink>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var usage = sp.GetService<IUsageTracker>();
            var retriever = sp.GetService<Retrieval.IRetriever>();
            var hookList = sp.GetServices<Hooks.IHook>()
                             .OrderBy(h => h.Order)
                             .ToArray();
            IReadOnlyList<Hooks.IHook>? hooks = hookList.Length == 0 ? null : hookList;
            return new AmpelumContext(chat, adapter, sink, loggerFactory)
            {
                Usage = usage,
                Retriever = retriever,
                Hooks = hooks,
            };
        });
        services.TryAddTransient(typeof(Predict<>));
        services.TryAddTransient(typeof(ChainOfThought<>));
        return new AmpelumBuilder(services);
    }
}
