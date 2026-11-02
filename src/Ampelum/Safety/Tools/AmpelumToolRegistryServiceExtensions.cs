using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable AMPELUM_V09_PREVIEW

namespace SignalGrid.Ampelum.Safety.Tools;

/// <summary>DI extensions for registering the tool registry.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class AmpelumToolRegistryServiceExtensions
{
    /// <summary>Adds an <see cref="InMemoryToolRegistry"/> singleton populated by the configure callback.</summary>
    public static IServiceCollection AddAmpelumToolRegistry(
        this IServiceCollection services,
        Action<InMemoryToolRegistry> configure)
    {
        var registry = new InMemoryToolRegistry();
        configure(registry);
        services.AddSingleton<IToolRegistry>(registry);
        return services;
    }
}
