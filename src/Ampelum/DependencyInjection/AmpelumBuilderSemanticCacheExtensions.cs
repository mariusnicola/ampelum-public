using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Caching.Semantic;
using SignalGrid.Ampelum.Hooks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace SignalGrid.Ampelum.DependencyInjection;

/// <summary>
/// Extension methods that wire <see cref="SemanticCacheHook"/> + <see cref="ISemanticCache"/>
/// into a configured <see cref="AmpelumBuilder"/> (PSQ-2, v0.9g).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class AmpelumBuilderSemanticCacheExtensions
{
    /// <summary>
    /// Registers an <see cref="InMemorySemanticCache"/> (capacity <paramref name="capacity"/>)
    /// and an <see cref="SemanticCacheHook"/> bound to it. Existing <see cref="ISemanticCache"/>
    /// registrations are preserved (TryAdd) so callers can pre-register a custom backing store.
    /// When an <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> of strings is registered (for example with
    /// <c>WithEmbeddingGenerator</c>), the hook also serves the most similar stored answer; without one it
    /// matches identical inputs only.
    /// </summary>
    /// <param name="builder">The builder to configure.</param>
    /// <param name="capacity">Maximum entry count for the default in-memory store.</param>
    /// <returns>The same builder for chaining.</returns>
    public static AmpelumBuilder AddSemanticCache(this AmpelumBuilder builder, int capacity = 256)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<ISemanticCache>(_ => new InMemorySemanticCache(capacity));
        builder.Services.AddSingleton<IHook>(sp =>
            new SemanticCacheHook(
                sp.GetRequiredService<ISemanticCache>(),
                sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>()));
        return builder;
    }
}
