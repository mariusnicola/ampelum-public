using System.ClientModel;
using System.CommandLine;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Repl;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;

namespace SignalGrid.Ampelum.Cli.Commands;

internal static class ReplCommand
{
    /// <summary>Env var carrying the OpenRouter API key — same convention as
    /// <see cref="ModelConfig"/> and the samples under <c>samples/</c>.</summary>
    internal const string OpenRouterApiKeyEnvVar = "OPENROUTER_API_KEY";

    public static Command Create() => Create(Console.In, Console.Out, Console.Error);

    internal static Command Create(TextReader input, TextWriter output, TextWriter error)
    {
        var cmd = new Command("repl", "Open the Ampelum interactive C# REPL.");
        cmd.SetAction(async (parse, ct) =>
        {
            var chat = ResolveChatClient();
            var ctx = new AmpelumContext(chat, new ChatAdapter(),
                NullTraceSink.Instance, NullLoggerFactory.Instance);
            var host = new ReplHost(new ReplGlobals(ctx));
            var loop = new ReplLoop(host, input, output, error);
            await loop.RunAsync(ct).ConfigureAwait(false);
            return 0;
        });
        return cmd;
    }

    /// <summary>
    /// Resolves a real <see cref="IChatClient"/> from environment configuration ONLY.
    /// No hardcoded default provider, no silent fallback (e.g. <c>FakeChatClient</c>) —
    /// model/provider selection belongs to <c>.env</c> exclusively. Throws a clear,
    /// actionable <see cref="InvalidOperationException"/> when nothing usable is configured.
    /// </summary>
    internal static IChatClient ResolveChatClient()
    {
        var apiKey = Environment.GetEnvironmentVariable(OpenRouterApiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"'ampelum repl' needs a chat provider configured via environment variables — " +
                $"set {OpenRouterApiKeyEnvVar} and OPENROUTER_MODEL_1 in the process environment. " +
                "There is no default provider or model. See .sample.env for the variable names.");
        }

        var options = new OpenAIClientOptions { Endpoint = new Uri("https://openrouter.ai/api/v1") };
        return new OpenAIClient(new ApiKeyCredential(apiKey), options)
            .GetChatClient(ModelConfig.DefaultModel)
            .AsIChatClient();
    }
}
