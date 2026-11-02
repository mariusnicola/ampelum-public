using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Ampelum.ContractTests;

internal sealed class CannedHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public readonly List<HttpRequestMessage> Requests = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(responder(request));
    }
}

/// <summary>
/// Shared helper that wires a <see cref="CannedHttpHandler"/> through the OpenAI SDK's
/// transport layer into an <see cref="IChatClient"/>.
/// </summary>
internal static class OpenAiTestHarness
{
    internal static IChatClient BuildChatClient(CannedHttpHandler handler)
    {
        var options = new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new OpenAIClient(new ApiKeyCredential("sk-test"), options);
#pragma warning disable OPENAI001 // AsIChatClient may be marked experimental
        return client.GetChatClient("gpt-4o-mini").AsIChatClient();
#pragma warning restore OPENAI001
    }
}
