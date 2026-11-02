using SignalGrid.Ampelum.Caching;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace SignalGrid.Ampelum.DependencyInjection;

/// <summary>
/// Extension methods that add response-caching capabilities to a <see cref="AmpelumBuilder"/>.
/// </summary>
public static class AmpelumBuilderCachingExtensions
{
    /// <summary>
    /// Wraps the registered <see cref="IChatClient"/> with a <see cref="AmpelumCachingChatClient"/>
    /// backed by <paramref name="cache"/>. Requires a prior <c>IChatClient</c> registration.
    /// Idempotent replacement of any earlier wrapping.
    /// </summary>
    /// <param name="builder">The <see cref="AmpelumBuilder"/> to configure.</param>
    /// <param name="cache">The <see cref="IDistributedCache"/> backing store for response caching.</param>
    /// <returns>The same <see cref="AmpelumBuilder"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="builder"/> or <paramref name="cache"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no <see cref="IChatClient"/> has been registered before calling this method.
    /// </exception>
    public static AmpelumBuilder WithResponseCache(this AmpelumBuilder builder, IDistributedCache cache)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(cache);

        var services = builder.Services;
        var prior = services.LastOrDefault(d => d.ServiceType == typeof(IChatClient))
            ?? throw new InvalidOperationException(
                "WithResponseCache requires IChatClient to be registered before calling.");

        services.Remove(prior);
        services.Add(new ServiceDescriptor(
            typeof(IChatClient),
            sp =>
            {
                IChatClient inner =
                    prior.ImplementationInstance as IChatClient
                    ?? prior.ImplementationFactory?.Invoke(sp) as IChatClient
                    ?? (IChatClient)ActivatorUtilities.CreateInstance(sp, prior.ImplementationType!);
                return new AmpelumCachingChatClient(inner, cache);
            },
            prior.Lifetime));

        return builder;
    }
}
