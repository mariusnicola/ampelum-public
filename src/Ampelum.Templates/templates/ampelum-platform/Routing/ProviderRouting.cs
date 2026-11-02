using Company.Platform.Tenancy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Company.Platform.Routing;

/// <summary>
/// Multi-provider routing stub.
/// </summary>
/// <remarks>
/// TODO: when v0.9g (AddAmpelumRouter) ships, replace the keyed registrations
/// here with the router. For now, name-keyed IChatClient instances let you
/// select a provider per tenant via TenantContext.Current.
/// </remarks>
public static class ProviderRouting
{
    public static IServiceCollection AddProviderRouting(this IServiceCollection services)
    {
        // Register two named providers; resolution is up to the caller via IServiceProvider.GetKeyedService.
        services.AddKeyedSingleton<IChatClient>("primary", (_, _) => new NullChatClient("primary"));
        services.AddKeyedSingleton<IChatClient>("secondary", (_, _) => new NullChatClient("secondary"));

        // Default IChatClient picks based on tenant.
        services.AddSingleton<IChatClient>(sp =>
        {
            var key = TenantContext.Current == "premium" ? "primary" : "secondary";
            return sp.GetRequiredKeyedService<IChatClient>(key);
        });
        return services;
    }
}

internal sealed class NullChatClient(string name) : IChatClient
{
    public string Name => name;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, $"[{name}] stub")]));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => AsyncEnumerable.Empty<ChatResponseUpdate>();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
