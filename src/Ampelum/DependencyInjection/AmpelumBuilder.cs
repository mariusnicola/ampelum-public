using SignalGrid.Ampelum.Usage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SignalGrid.Ampelum.DependencyInjection;

/// <summary>
/// Fluent builder returned by <see cref="ServiceCollectionExtensions.AddAmpelum"/> that allows
/// customising the adapter, trace sink, and usage tracker registered in the DI container.
/// </summary>
public sealed class AmpelumBuilder(IServiceCollection services)
{
    /// <summary>The underlying <see cref="IServiceCollection"/> for advanced registration scenarios.</summary>
    public IServiceCollection Services { get; } = services;

    /// <summary>
    /// Replaces the default <see cref="IAdapter"/> registration with <typeparamref name="TAdapter"/> (singleton lifetime).
    /// </summary>
    /// <typeparam name="TAdapter">Concrete adapter type to register.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public AmpelumBuilder UseAdapter<TAdapter>() where TAdapter : class, IAdapter
    {
        Services.RemoveAll<IAdapter>();
        Services.AddSingleton<IAdapter, TAdapter>();
        return this;
    }

    /// <summary>
    /// Replaces the default <see cref="ITraceSink"/> registration with <typeparamref name="TSink"/> (scoped lifetime).
    /// </summary>
    /// <typeparam name="TSink">Concrete trace sink type to register.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public AmpelumBuilder UseTraceSink<TSink>() where TSink : class, ITraceSink
    {
        Services.RemoveAll<ITraceSink>();
        Services.AddScoped<ITraceSink, TSink>();
        return this;
    }

    /// <summary>
    /// Opt out of the v0.7 default <see cref="BatchedTraceSink"/> wrapper and register
    /// <typeparamref name="TSink"/> as the synchronous <see cref="ITraceSink"/>. Use this when the
    /// inner sink is already non-blocking (e.g. <see cref="NullTraceSink"/> or an in-memory list)
    /// or when testing requires immediate write semantics.
    /// </summary>
    /// <typeparam name="TSink">Concrete synchronous trace sink type to register.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public AmpelumBuilder UseSynchronousTraceSink<TSink>() where TSink : class, ITraceSink
    {
        Services.RemoveAll<ITraceSink>();
        Services.AddScoped<ITraceSink, TSink>();
        return this;
    }

    /// <summary>
    /// Replaces the default <see cref="IUsageTracker"/> registration with <typeparamref name="TTracker"/> (scoped lifetime).
    /// </summary>
    /// <typeparam name="TTracker">Concrete usage tracker type to register.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public AmpelumBuilder WithUsageTracker<TTracker>() where TTracker : class, IUsageTracker
    {
        Services.RemoveAll<IUsageTracker>();
        Services.AddScoped<IUsageTracker, TTracker>();
        return this;
    }

    /// <summary>
    /// Registers <typeparamref name="TRetriever"/> as the <see cref="Retrieval.IRetriever"/> implementation
    /// with <b>Scoped</b> lifetime (spec Â§3.4: may hold per-request context).
    /// Replaces any previous <see cref="Retrieval.IRetriever"/> registration.
    /// </summary>
    /// <typeparam name="TRetriever">Concrete retriever type to register.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public AmpelumBuilder WithRetriever<TRetriever>()
        where TRetriever : class, Retrieval.IRetriever
    {
        Services.RemoveAll<Retrieval.IRetriever>();
        Services.AddScoped<Retrieval.IRetriever, TRetriever>();
        return this;
    }

    /// <summary>
    /// Registers <typeparamref name="TVectorStore"/> as the <see cref="Retrieval.IVectorStore"/> implementation
    /// with <b>Singleton</b> lifetime (spec Â§3.4: stateless connection pool; shared safely).
    /// Replaces any previous <see cref="Retrieval.IVectorStore"/> registration.
    /// </summary>
    /// <typeparam name="TVectorStore">Concrete vector store type to register.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public AmpelumBuilder WithVectorStore<TVectorStore>()
        where TVectorStore : class, Retrieval.IVectorStore
    {
        Services.RemoveAll<Retrieval.IVectorStore>();
        Services.AddSingleton<Retrieval.IVectorStore, TVectorStore>();
        return this;
    }

    /// <summary>
    /// Registers an <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> factory with
    /// <b>Singleton</b> lifetime (spec Â§3.4: matches MEAI convention; stateless client wrapper).
    /// Replaces any previous <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> registration.
    /// </summary>
    /// <param name="factory">Factory delegate invoked once to create the singleton instance.</param>
    /// <returns>This builder for chaining.</returns>
    public AmpelumBuilder WithEmbeddingGenerator(
        Func<IServiceProvider, IEmbeddingGenerator<string, Embedding<float>>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Services.RemoveAll<IEmbeddingGenerator<string, Embedding<float>>>();
        Services.AddSingleton(factory);
        return this;
    }

    /// <summary>
    /// Registers <see cref="Persistence.JsonProgramPersistence"/> as the
    /// <see cref="Persistence.IProgramPersistence"/> singleton (spec Â§3.4).
    /// Uses <c>TryAddSingleton</c> â€” idempotent and preserves any prior custom
    /// registration (PSQ-5).
    /// </summary>
    /// <returns>This builder for chaining.</returns>
    public AmpelumBuilder AddProgramPersistence()
    {
        Services.TryAddSingleton<Persistence.IProgramPersistence,
                                  Persistence.JsonProgramPersistence>();
        return this;
    }

    /// <summary>
    /// Registers <typeparamref name="THook"/> as an <see cref="Hooks.IHook"/> singleton.
    /// Multiple hooks may be registered; <see cref="ServiceCollectionExtensions.AddAmpelum"/>
    /// collects all registered <see cref="Hooks.IHook"/> services into <see cref="AmpelumContext.Hooks"/>
    /// sorted by <see cref="Hooks.IHook.Order"/> ascending.
    /// </summary>
    /// <typeparam name="THook">Concrete hook type to register.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public AmpelumBuilder AddAmpelumHook<THook>() where THook : class, Hooks.IHook
    {
        Services.AddSingleton<Hooks.IHook, THook>();
        return this;
    }

    /// <summary>
    /// Opt-in registration of <see cref="Audit.BatchedAuditSink"/> wrapping <typeparamref name="TInner"/>.
    /// Audit batching is opt-in (not default) because audit-chain integrity requires zero-loss
    /// semantics: when the queue is full, the producer back-pressures rather than dropping.
    /// </summary>
    /// <typeparam name="TInner">Concrete inner audit sink type to wrap.</typeparam>
    /// <param name="capacity">Maximum queue depth before AppendAsync awaits drain. Defaults to 1000.</param>
    /// <returns>This builder for chaining.</returns>
    public AmpelumBuilder AddBatchedAuditSink<TInner>(int capacity = 1000)
        where TInner : class, Audit.IAuditSink
    {
        Services.RemoveAll<Audit.IAuditSink>();
        Services.AddScoped<TInner>();
        Services.AddScoped<Audit.IAuditSink>(sp =>
            new Audit.BatchedAuditSink(sp.GetRequiredService<TInner>(), capacity));
        return this;
    }
}
