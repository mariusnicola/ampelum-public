using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SignalGrid.Ampelum.DependencyInjection;

/// <summary>
/// Extension methods that wire <see cref="RoutedChatClient"/> into a configured
/// <see cref="AmpelumBuilder"/> (PSQ-1, v0.9g).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class AmpelumBuilderRoutingExtensions
{
    /// <summary>
    /// Registers a <see cref="ProviderEndpoint"/> that routing strategies will pick from.
    /// Multiple endpoints may be registered; the order of registration is preserved.
    /// </summary>
    /// <param name="builder">The builder to configure.</param>
    /// <param name="endpoint">The endpoint definition.</param>
    /// <returns>The same builder for chaining.</returns>
    public static AmpelumBuilder AddProviderEndpoint(this AmpelumBuilder builder, ProviderEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpoint);
        builder.Services.AddSingleton(endpoint);
        return builder;
    }

    /// <summary>
    /// Replaces the registered <see cref="IChatClient"/> with a <see cref="RoutedChatClient"/>
    /// driven by <typeparamref name="TStrategy"/>. Requires at least one prior
    /// <see cref="AddProviderEndpoint"/> call.
    /// </summary>
    /// <typeparam name="TStrategy">Strategy type (resolved through DI; instantiated once).</typeparam>
    /// <param name="builder">The builder to configure.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no provider endpoint has been registered.</exception>
    public static AmpelumBuilder AddRouting<TStrategy>(this AmpelumBuilder builder)
        where TStrategy : class, IRoutingStrategy
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<IRoutingStrategy, TStrategy>();

        var services = builder.Services;
        // Strip any previous IChatClient registration so the router becomes the canonical
        // surface; endpoints carry their own inner clients.
        services.RemoveAll<IChatClient>();
        services.AddSingleton<IChatClient>(sp =>
        {
            var endpoints = sp.GetServices<ProviderEndpoint>().ToArray();
            if (endpoints.Length == 0)
                throw new InvalidOperationException(
                    "AddRouting<T> requires at least one ProviderEndpoint registered via AddProviderEndpoint.");
            var strategy = sp.GetRequiredService<IRoutingStrategy>();
            return new RoutedChatClient(endpoints, strategy);
        });
        return builder;
    }
}
