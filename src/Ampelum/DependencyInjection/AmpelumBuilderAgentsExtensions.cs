using System;
using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Agents.Memory;
using SignalGrid.Ampelum.Agents.SpeakerSelectors;
using SignalGrid.Ampelum.Agents.Tools;
using SignalGrid.Ampelum.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace SignalGrid.Ampelum.DependencyInjection;

/// <summary>v0.9a DI extensions for multi-agent + memory + realtime registration.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
#pragma warning disable AMPELUM_V09_PREVIEW
public static class AmpelumBuilderAgentsExtensions
{
    /// <summary>
    /// Registers default v0.9a agent services (idempotent via TryAddSingleton):
    /// <see cref="IAgentToolRegistry"/> as singleton <see cref="AgentToolRegistry"/>;
    /// <see cref="ISpeakerSelector"/> as singleton <see cref="RoundRobinSelector"/>.
    /// </summary>
    public static AmpelumBuilder AddAgents(this AmpelumBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<IAgentToolRegistry, AgentToolRegistry>();
        builder.Services.TryAddSingleton<ISpeakerSelector, RoundRobinSelector>();
        return builder;
    }

    /// <summary>
    /// Registers <typeparamref name="T"/> as the default <see cref="IConversationMemory"/> singleton (TryAdd).
    /// Resolves <typeparamref name="T"/> from the container â€” the caller is expected to have
    /// already registered any constructor dependencies of <typeparamref name="T"/>.
    /// </summary>
    public static AmpelumBuilder AddConversationMemory<T>(this AmpelumBuilder builder)
        where T : class, IConversationMemory
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<IConversationMemory>(sp => sp.GetRequiredService<T>());
        return builder;
    }

    /// <summary>
    /// Registers <see cref="RealtimeChatClient"/> as a singleton bound to the given endpoint and API key.
    /// </summary>
    public static AmpelumBuilder AddRealtimeChatClient(this AmpelumBuilder builder, Uri endpoint, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(apiKey);
        builder.Services.AddSingleton(sp => new RealtimeChatClient(
            endpoint,
            apiKey,
            socket: null,
            logger: sp.GetService<ILogger<RealtimeChatClient>>()));
        return builder;
    }
}
#pragma warning restore AMPELUM_V09_PREVIEW
