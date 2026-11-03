using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Caching;
using SignalGrid.Ampelum.Caching.Semantic;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Routing;
using SignalGrid.Ampelum.Streaming;

namespace Ampelum.Samples.ChatFrontend;

/// <summary>A one-shot reply and the endpoint that served it.</summary>
public sealed record ChatReply(string Text, string? Endpoint);

/// <summary>
/// The backend of a customer chat widget. Every model call goes through one router: the primary
/// endpoint first, the fallback endpoint when the primary throws.
/// </summary>
public sealed class ChatBackend : IDisposable
{
    public const string PrimaryEndpoint = "primary";
    public const string FallbackEndpoint = "fallback";
    public const string SystemPrompt = "You are the support chat of an online shop. Answer briefly and politely.";

    readonly ServiceProvider _provider;
    readonly IServiceScope _scope;
    readonly TokenCache _tokenCache = new();
    readonly TokenCachedChatClient _quick;

    /// <summary>
    /// Each endpoint has a provider client and the model id sent to it; the two clients may be the same
    /// instance. <paramref name="hooks"/> are extra hooks, for example moderation or PII redaction.
    /// </summary>
    public ChatBackend(
        IChatClient primary, string primaryModelId, IChatClient fallback, string fallbackModelId, IEnumerable<IHook>? hooks = null)
    {
        var routed = new RoutedChatClient(
            [
                new ProviderEndpoint(PrimaryEndpoint, new PinnedModelClient(primary, primaryModelId)),
                new ProviderEndpoint(FallbackEndpoint, new PinnedModelClient(fallback, fallbackModelId)),
            ],
            new Failover());

        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(routed);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddAmpelum().AddSemanticCache();
        foreach (var hook in hooks ?? [])
            services.AddSingleton(hook);
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
        Context = _scope.ServiceProvider.GetRequiredService<AmpelumContext>();
        _quick = new TokenCachedChatClient(routed, _tokenCache);
    }

    /// <summary>The live backend: both model ids and the key are required settings, with no fallback from one to the other.</summary>
    public static ChatBackend Live(Func<string, string> require, Func<IChatClient> createClient)
    {
        var primaryId = require("OPENROUTER_MODEL_1");
        var fallbackId = require("OPENROUTER_MODEL_2");
        var client = createClient();
        return new ChatBackend(client, primaryId, client, fallbackId);
    }

    public AmpelumContext Context { get; }

    /// <summary>Entries the semantic cache holds.</summary>
    public int CachedAnswers => _provider.GetRequiredService<ISemanticCache>().Count;

    /// <summary>Starts one conversation. Its replies arrive token by token.</summary>
    public StreamingChatSession OpenConversation() => new(Context, SystemPrompt);

    /// <summary>A typed answer. A repeated identical question is served by the semantic cache hook.</summary>
    public async Task<ReplySignature> AskAsync(string question, CancellationToken ct = default)
    {
        var predict = _scope.ServiceProvider.GetRequiredService<Predict<ReplySignature>>();
        return (await predict.ForwardAsync(new ReplySignature { Question = question }, ct: ct).ConfigureAwait(false)).Value;
    }

    /// <summary>The typed fields of an answer, as they are produced.</summary>
    public IAsyncEnumerable<StreamUpdate<ReplySignature>> StreamFieldsAsync(string question, CancellationToken ct = default)
    {
        var predict = _scope.ServiceProvider.GetRequiredService<Predict<ReplySignature>>();
        return ProgramStreamer.Streamify<ReplyProgram, ReplySignature>(
            new ReplyProgram(predict, question), ReplyProgram.PrimaryField, ct);
    }

    /// <summary>A plain text reply. A repeated identical prompt never reaches the model again.</summary>
    public async Task<ChatReply> QuickReplyAsync(string prompt, CancellationToken ct = default)
    {
        var response = await _quick
            .GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct)
            .ConfigureAwait(false);
        var endpoint = response.AdditionalProperties is { } props
            && props.TryGetValue(HookMetadataKeys.RouterTargetName, out var name) ? name as string : null;
        return new ChatReply(response.Text, endpoint);
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
        _tokenCache.Dispose();
    }
}
