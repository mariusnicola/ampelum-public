using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace SignalGrid.Ampelum.Observability.LangFuse;

/// <summary>DI helpers for wiring <see cref="LangFuseExporter"/> onto an <see cref="AmpelumBuilder"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class AmpelumBuilderLangFuseExtensions
{
    /// <summary>Decorates the registered <see cref="ITraceSink"/> with a <see cref="LangFuseExporter"/>.</summary>
    public static AmpelumBuilder AddLangFuseExporter(
        this AmpelumBuilder builder,
        Action<LangFuseCredentials> configureCredentials,
        Action<LangFuseExporterOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureCredentials);

        builder.Services.Configure(configureCredentials);
        if (configureOptions is not null) builder.Services.Configure(configureOptions);
        else builder.Services.AddOptions<LangFuseExporterOptions>();

        builder.Services.TryAddSingleton<LangFuseEventMapper>();
        builder.Services.AddHttpClient<ILangFuseClient, LangFuseClient>();

        var prior = builder.Services.LastOrDefault(d => d.ServiceType == typeof(ITraceSink))
            ?? throw new InvalidOperationException(
                "AddLangFuseExporter requires an existing ITraceSink registration.");
        builder.Services.Remove(prior);
        builder.Services.AddScoped<ITraceSink>(sp =>
        {
            ITraceSink inner;
            if (prior.ImplementationInstance is ITraceSink i) inner = i;
            else if (prior.ImplementationFactory is { } f) inner = (ITraceSink)f(sp)!;
            else if (prior.ImplementationType is { } t) inner = (ITraceSink)ActivatorUtilities.CreateInstance(sp, t);
            else throw new InvalidOperationException("Prior ITraceSink registration not resolvable.");
            return new LangFuseExporter(
                inner,
                sp.GetRequiredService<ILangFuseClient>(),
                sp.GetRequiredService<LangFuseEventMapper>(),
                sp.GetRequiredService<IOptions<LangFuseExporterOptions>>());
        });
        return builder;
    }
}
