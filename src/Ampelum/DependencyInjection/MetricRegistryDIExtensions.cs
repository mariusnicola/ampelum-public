using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SignalGrid.Ampelum.DependencyInjection;

/// <summary>DI helpers for <see cref="IMetricRegistry"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class MetricRegistryDIExtensions
{
    /// <summary>
    /// Registers an <see cref="IMetricRegistry"/> singleton built from the supplied configuration callback.
    /// Replaces any prior <see cref="IMetricRegistry"/> registration.
    /// </summary>
    /// <param name="builder">The <see cref="AmpelumBuilder"/> to extend.</param>
    /// <param name="configure">Callback that populates the <see cref="MetricRegistryBuilder"/>.</param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    public static AmpelumBuilder AddMetricRegistry(
        this AmpelumBuilder builder,
        Action<MetricRegistryBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var b = new MetricRegistryBuilder();
        configure(b);
        var registry = b.Build();
        builder.Services.RemoveAll<IMetricRegistry>();
        builder.Services.AddSingleton(registry);
        return builder;
    }
}
