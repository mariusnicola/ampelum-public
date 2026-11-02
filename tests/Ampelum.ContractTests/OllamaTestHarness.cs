using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Ampelum.ContractTests;

/// <summary>
/// Creates an <see cref="IChatClient"/> backed by a local Ollama instance via its
/// OpenAI-compatible endpoint (http://localhost:11434/v1). Tests must call
/// <see cref="IsRunningAsync"/> and <see cref="IsModelPulledAsync"/> before using
/// <see cref="BuildChatClient"/> and fail with an actionable message if either returns false.
/// </summary>
internal static class OllamaTestHarness
{
    private static readonly Uri OllamaBase = new("http://localhost:11434");

    /// <summary>Returns true when Ollama is listening on the default port.</summary>
    internal static async Task<bool> IsRunningAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var resp = await http.GetAsync(new Uri(OllamaBase, "/api/tags"), ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Returns true when <paramref name="model"/> is already pulled in the local Ollama instance.</summary>
    internal static async Task<bool> IsModelPulledAsync(string model, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var resp = await http.GetAsync(new Uri(OllamaBase, "/api/tags"), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Contains($"\"{model}\"", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>
    /// Builds an <see cref="IChatClient"/> targeting the local Ollama OpenAI-compatible
    /// endpoint. The model must already be pulled (<c>ollama pull <paramref name="model"/></c>).
    /// </summary>
    internal static IChatClient BuildChatClient(string model)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(OllamaBase, "/v1") };
        var client = new OpenAIClient(new ApiKeyCredential("ollama"), options);
#pragma warning disable OPENAI001
        return client.GetChatClient(model).AsIChatClient();
#pragma warning restore OPENAI001
    }
}
