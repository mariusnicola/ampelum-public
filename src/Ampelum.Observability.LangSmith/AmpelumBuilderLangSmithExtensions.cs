using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SignalGrid.Ampelum.Observability.LangSmith;

/// <summary>DI helpers for wiring <see cref="LangSmithExporter"/> onto an <see cref="AmpelumBuilder"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class AmpelumBuilderLangSmithExtensions
{
    /// <summary>
    /// Decorates the registered <see cref="ITraceSink"/> with a <see cref="LangSmithExporter"/>.
    /// </summary>
    public static AmpelumBuilder AddLangSmithExporter(
        this AmpelumBuilder builder,
        Action<LangSmithCredentials> configureCredentials,
        Action<LangSmithExporterOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureCredentials);

        builder.Services.Configure(configureCredentials);
        if (configureOptions is not null)
        {
            builder.Services.Configure(configureOptions);
        }
        else
        {
            builder.Services.AddOptions<LangSmithExporterOptions>();
        }

        builder.Services.TryAddSingleton<LangSmithRunMapper>();
        builder.Services.AddHttpClient<ILangSmithClient, LangSmithClient>();

        // Manual decoration: capture the previous ITraceSink registration and wrap it.
        var prior = builder.Services.LastOrDefault(d => d.ServiceType == typeof(ITraceSink));
        if (prior is null)
        {
            throw new InvalidOperationException(
                "AddLangSmithExporter requires an existing ITraceSink registration. " +
                "Call AddAmpelum(...) first, then AddLangSmithExporter.");
        }
        builder.Services.Remove(prior);
        builder.Services.AddScoped<ITraceSink>(sp =>
        {
            var inner = ActivatorUtilities.CreateInstance<InnerTraceSinkResolver>(sp, prior).Resolve(sp);
            var client = sp.GetRequiredService<ILangSmithClient>();
            var mapper = sp.GetRequiredService<LangSmithRunMapper>();
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LangSmithExporterOptions>>();
            var creds = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LangSmithCredentials>>();
            return new LangSmithExporter(inner, client, mapper, options, creds);
        });

        return builder;
    }

    sealed class InnerTraceSinkResolver(ServiceDescriptor descriptor)
    {
        readonly ServiceDescriptor _descriptor = descriptor;
        public ITraceSink Resolve(IServiceProvider sp)
        {
            if (_descriptor.ImplementationInstance is ITraceSink instance) return instance;
            if (_descriptor.ImplementationFactory is { } factory)
                return (ITraceSink)factory(sp)!;
            if (_descriptor.ImplementationType is { } type)
                return (ITraceSink)ActivatorUtilities.CreateInstance(sp, type);
            throw new InvalidOperationException("ITraceSink registration has no resolvable implementation.");
        }
    }
}
