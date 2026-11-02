using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace SignalGrid.Ampelum.Observability.Phoenix;

/// <summary>DI helpers for wiring <see cref="PhoenixExporter"/> onto an <see cref="AmpelumBuilder"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class AmpelumBuilderPhoenixExtensions
{
    /// <summary>Decorates the registered <see cref="ITraceSink"/> with a <see cref="PhoenixExporter"/>.</summary>
    public static AmpelumBuilder AddPhoenixExporter(
        this AmpelumBuilder builder,
        Action<PhoenixCredentials>? configureCredentials = null,
        Action<PhoenixExporterOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (configureCredentials is not null) builder.Services.Configure(configureCredentials);
        else builder.Services.AddOptions<PhoenixCredentials>();
        if (configureOptions is not null) builder.Services.Configure(configureOptions);
        else builder.Services.AddOptions<PhoenixExporterOptions>();

        builder.Services.TryAddSingleton<PhoenixSpanMapper>();

        var prior = builder.Services.LastOrDefault(d => d.ServiceType == typeof(ITraceSink))
            ?? throw new InvalidOperationException(
                "AddPhoenixExporter requires an existing ITraceSink registration.");
        builder.Services.Remove(prior);
        builder.Services.AddScoped<ITraceSink>(sp =>
        {
            ITraceSink inner;
            if (prior.ImplementationInstance is ITraceSink i) inner = i;
            else if (prior.ImplementationFactory is { } f) inner = (ITraceSink)f(sp)!;
            else if (prior.ImplementationType is { } t) inner = (ITraceSink)ActivatorUtilities.CreateInstance(sp, t);
            else throw new InvalidOperationException("Prior ITraceSink registration not resolvable.");
            return new PhoenixExporter(
                inner,
                sp.GetRequiredService<PhoenixSpanMapper>(),
                sp.GetRequiredService<IOptions<PhoenixExporterOptions>>());
        });
        return builder;
    }
}
