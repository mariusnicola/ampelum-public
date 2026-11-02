using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;

namespace Ampelum.Samples.Shared;

/// <summary>Reads sample settings from appsettings.json, .env and environment variables, in rising priority.</summary>
public sealed class SampleConfig
{
    const string OpenRouterEndpoint = "https://openrouter.ai/api/v1";

    readonly IConfiguration _configuration;

    SampleConfig(IConfiguration configuration) => _configuration = configuration;

    /// <summary>Loads settings, searching upward from <paramref name="startDirectory"/> for both files.</summary>
    public static SampleConfig Load(string? startDirectory = null)
    {
        var start = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
        var builder = new ConfigurationBuilder();
        var json = FindUpward(start, "appsettings.json");
        if (json is not null)
            builder.AddJsonFile(json, optional: false);
        var env = FindUpward(start, ".env");
        if (env is not null)
            builder.AddInMemoryCollection(ReadDotEnv(env));
        builder.AddEnvironmentVariables();
        return new SampleConfig(builder.Build());
    }

    /// <summary>Returns a required setting. A missing value is an error because nothing has a code default.</summary>
    public string Require(string key)
    {
        var value = _configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Missing setting {key}. Copy .sample.env to .env and set it.");
        return value;
    }

    /// <summary>Builds the live chat client from OPENROUTER_API_KEY and OPENROUTER_MODEL_1.</summary>
    public IChatClient CreateChatClient()
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(OpenRouterEndpoint) };
        return new OpenAIClient(new ApiKeyCredential(Require("OPENROUTER_API_KEY")), options)
            .GetChatClient(Require("OPENROUTER_MODEL_1"))
            .AsIChatClient();
    }

    static string? FindUpward(DirectoryInfo? directory, string fileName)
    {
        for (; directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    static Dictionary<string, string?> ReadDotEnv(string path)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            var separator = line.IndexOf('=');
            if (line.Length == 0 || line[0] == '#' || separator <= 0)
                continue;
            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return values;
    }
}
