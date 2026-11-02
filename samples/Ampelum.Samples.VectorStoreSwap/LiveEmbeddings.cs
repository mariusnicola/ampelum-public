using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Ampelum.Samples.VectorStoreSwap;

public static class LiveEmbeddings
{
    /// <summary>Builds an embedding generator for any OpenAI-compatible endpoint named in the settings.</summary>
    public static IEmbeddingGenerator<string, Embedding<float>> Create(Func<string, string> setting)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(setting(SettingNames.EmbeddingEndpoint)) };
        return new OpenAIClient(new ApiKeyCredential(setting(SettingNames.EmbeddingApiKey)), options)
            .GetEmbeddingClient(setting(SettingNames.EmbeddingModel))
            .AsIEmbeddingGenerator();
    }
}
