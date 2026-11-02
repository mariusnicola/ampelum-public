using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using System.Text;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Safety.Pii;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace SignalGrid.Ampelum.Retrieval.Patterns;

/// <summary>HyDE: generate hypothetical answer document via LM, retrieve real docs by similarity to that hypothesis.</summary>
/// <remarks>
/// Audit: stores XxHash64 of hypothesis text in activity tag <c>ampelum.hyde.hypothesis_hash</c>.
/// When an <see cref="IPiiRedactor"/> is supplied, the hypothesis text is redacted before hashing
/// AND before being forwarded to the inner retriever â€” keeping the audit hash consistent with what
/// downstream sees. Default <c>null</c> preserves the v0.9b behavior (plain-text hash and forward).
/// </remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class HydeRetriever : IRetriever
{
    readonly IRetriever _inner;
    readonly IChatClient _chat;
    readonly HydeOptions _options;
    readonly IPiiRedactor? _piiRedactor;

    /// <summary>Initialises the HyDE retriever.</summary>
    /// <param name="inner">Underlying retriever the redacted hypothesis is forwarded to.</param>
    /// <param name="chat">Chat client used to generate the hypothesis.</param>
    /// <param name="options">HyDE configuration.</param>
    /// <param name="piiRedactor">
    /// Optional PII redactor. When supplied, the hypothesis is redacted before hashing and forwarding;
    /// when null (default), behaviour matches the v0.9b shipped surface (plain-text).
    /// </param>
    public HydeRetriever(
        IRetriever inner,
        IChatClient chat,
        HydeOptions options,
        IPiiRedactor? piiRedactor = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(options);
        _inner = inner;
        _chat = chat;
        _options = options;
        _piiRedactor = piiRedactor;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
        string query,
        int topK,
        IReadOnlyDictionary<string, object?>? filters = null,
        CancellationToken ct = default)
    {
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.retriever.hyde.hypothesis");
        if (!_options.IsEnabled)
        {
            return await _inner.RetrieveAsync(query, topK, filters, ct).ConfigureAwait(false);
        }
        var msgs = new[]
        {
            new ChatMessage(ChatRole.System, _options.SystemPrompt),
            new ChatMessage(ChatRole.User, query),
        };
        var chatOpts = _options.ChatOptions ?? new ChatOptions { MaxOutputTokens = _options.HypothesisTokens };
        var hookCtx = AmpelumContext.Ambient is { } ambient
            ? ambient with { Chat = _chat }
            : new AmpelumContext(_chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var response = await HookedLmCall.InvokeAsync(
            hookCtx, "HydeRetriever.GenerateHypothesis", msgs, chatOpts, ct).ConfigureAwait(false);
        var hypothesis = _piiRedactor?.Redact(response.Text) ?? response.Text;
        Span<byte> hash = stackalloc byte[8];
        XxHash64.Hash(Encoding.UTF8.GetBytes(hypothesis), hash);
        activity?.SetTag("SignalGrid.Ampelum.hyde.hypothesis_length", hypothesis.Length);
        activity?.SetTag("SignalGrid.Ampelum.hyde.hypothesis_hash", Convert.ToHexString(hash));
        activity?.SetTag("SignalGrid.Ampelum.hyde.redacted", _piiRedactor is not null);
        return await _inner.RetrieveAsync(hypothesis, topK, filters, ct).ConfigureAwait(false);
    }
}
