using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.RateLimiting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SignalGrid.Ampelum.DependencyInjection;

/// <summary>
/// Extension methods that wire <see cref="RateLimitedChatClient"/> into a configured
/// <see cref="AmpelumBuilder"/> (PSQ-3, v0.9g).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class AmpelumBuilderRateLimitingExtensions
{
    /// <summary>
    /// Wraps the registered <see cref="IChatClient"/> with a <see cref="RateLimitedChatClient"/>
    /// using the supplied <paramref name="options"/>. The previous registration is replaced.
    /// </summary>
    /// <param name="builder">The builder to configure.</param>
    /// <param name="options">Bucket configuration. Pass <see langword="null"/> for defaults.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no inner <see cref="IChatClient"/> is registered.</exception>
    public static AmpelumBuilder AddRateLimit(this AmpelumBuilder builder, RateLimitingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var opts = options ?? new RateLimitingOptions();

        var existing = builder.Services.LastOrDefault(d => d.ServiceType == typeof(IChatClient))
            ?? throw new InvalidOperationException(
                "AddRateLimit requires an existing IChatClient registration; call AddAmpelum / AddRouting first.");

        builder.Services.RemoveAll<IChatClient>();
        builder.Services.AddSingleton<IChatClient>(sp =>
        {
            var inner = ResolveInner(sp, existing);
            return new RateLimitedChatClient(inner, opts);
        });
        return builder;
    }

    static IChatClient ResolveInner(IServiceProvider sp, ServiceDescriptor desc)
    {
        if (desc.ImplementationInstance is IChatClient inst) return inst;
        if (desc.ImplementationFactory is { } factory) return (IChatClient)factory(sp);
        // ServiceDescriptor invariant: exactly one of {Instance, Factory, Type} is non-null.
        return (IChatClient)ActivatorUtilities.CreateInstance(sp, desc.ImplementationType!);
    }
}
