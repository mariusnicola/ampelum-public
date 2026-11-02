using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;

namespace SignalGrid.Ampelum.Caching;

/// <summary>
/// Subclass of MEAI's <see cref="DistributedCachingChatClient"/> with an explicit SHA256 key
/// formula per spec Â§4.7.1. Annotates cache-hit responses via
/// <c>ChatResponse.AdditionalProperties["SignalGrid.Ampelum.cache_hit"] = true</c>.
/// </summary>
/// <remarks>
/// Key is computed from six fields separated by ASCII unit-separator (0x1F):
/// model, temperature, system messages, non-system messages, max_tokens, response_format.
/// The following fields are intentionally excluded: TopP, Seed, Tools, StopSequences.
/// </remarks>
public class AmpelumCachingChatClient : DistributedCachingChatClient
{
    // ASCII control separators used as unambiguous field delimiters
    const byte UnitSep = 0x1F;    // field separator within the hash buffer
    const char RecordSep = (char)0x1E; // record separator used when joining multi-message text
    const char GroupSep = (char)0x1D;  // group separator used between role and text

    /// <summary>
    /// Initialises a new <see cref="AmpelumCachingChatClient"/> that wraps <paramref name="innerClient"/>
    /// and persists serialised responses in <paramref name="storage"/>.
    /// </summary>
    /// <param name="innerClient">The downstream <see cref="IChatClient"/> to delegate cache misses to.</param>
    /// <param name="storage">The <see cref="IDistributedCache"/> backing store.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="innerClient"/> or <paramref name="storage"/> is <see langword="null"/>.</exception>
    public AmpelumCachingChatClient(IChatClient innerClient, IDistributedCache storage)
        : base(innerClient, storage)
    {
        // Null validation for both parameters is performed by the base constructor chain.
        // ArgumentNullException for innerClient: thrown by DelegatingChatClient.
        // ArgumentNullException for storage: thrown by DistributedCachingChatClient.
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Spec Â§4.7.1 mentions an explicit <c>"stream"</c> discriminator written at the end of the hash
    /// buffer. This is intentionally omitted here: MEAI's <c>CachingChatClient</c> already provides
    /// the discriminator by passing a boxed <see langword="true"/> (streaming) or <see langword="false"/>
    /// (non-streaming) as the first element of <paramref name="additionalValues"/> when it invokes this
    /// method, so the <c>foreach (var v in additionalValues)</c> loop below naturally includes it in the
    /// SHA-256 input. Adding a redundant <c>"stream"</c> field would produce a different key from what
    /// the base class expects, breaking cross-call cache sharing.
    /// </remarks>
    protected override string GetCacheKey(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        params ReadOnlySpan<object?> additionalValues)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();

        using var buf = new System.IO.MemoryStream();
        WriteField(buf, Utf8(options?.ModelId ?? ""));                        // 1 model
        WriteField(buf, Utf8(FormatTemperature(options?.Temperature)));       // 2 temperature
        WriteField(buf, Utf8(JoinSystemText(list)));                          // 3 system messages
        WriteField(buf, Utf8(JoinNonSystemText(list)));                       // 4 non-system messages
        WriteField(buf, Utf8(FormatMaxTokens(options?.MaxOutputTokens)));     // 5 max_tokens
        WriteField(buf, Utf8(FormatResponseFormat(options?.ResponseFormat))); // 6 response_format
        foreach (var v in additionalValues)
            WriteField(buf, Utf8(v?.ToString() ?? "null")); // includes MEAI's stream/non-stream discriminator

        return Convert.ToHexStringLower(SHA256.HashData(buf.ToArray()));
    }

    /// <summary>
    /// Reads a cached response by key. If a hit is found, stamps
    /// <c>AdditionalProperties["SignalGrid.Ampelum.cache_hit"] = true</c> before returning.
    /// </summary>
    protected override async Task<ChatResponse?> ReadCacheAsync(string key, CancellationToken cancellationToken)
    {
        var response = await base.ReadCacheAsync(key, cancellationToken).ConfigureAwait(false);
        if (response is not null)
        {
            E2ETrace.Log($"CACHE HIT (non-streaming) key={key[..Math.Min(8, key.Length)]}...");
            (response.AdditionalProperties ??= new AdditionalPropertiesDictionary())["SignalGrid.Ampelum.cache_hit"] = true;
        }
        else
            E2ETrace.Log($"CACHE MISS (non-streaming) key={key[..Math.Min(8, key.Length)]}...");
        return response;
    }

    /// <summary>
    /// Reads cached streaming updates by key. If a hit is found, stamps
    /// <c>AdditionalProperties["SignalGrid.Ampelum.cache_hit"] = true</c> on the first update before returning.
    /// </summary>
    protected override async Task<IReadOnlyList<ChatResponseUpdate>?> ReadCacheStreamingAsync(
        string key, CancellationToken cancellationToken)
    {
        var updates = await base.ReadCacheStreamingAsync(key, cancellationToken).ConfigureAwait(false);
        if (updates is not null && updates.Count > 0)
        {
            E2ETrace.Log($"CACHE HIT (streaming) key={key[..Math.Min(8, key.Length)]}...");
            (updates[0].AdditionalProperties ??= new AdditionalPropertiesDictionary())["SignalGrid.Ampelum.cache_hit"] = true;
        }
        else
            E2ETrace.Log($"CACHE MISS (streaming) key={key[..Math.Min(8, key.Length)]}...");
        return updates;
    }

    // ---- private helpers -----------------------------------------------------------------------

    static void WriteField(System.IO.MemoryStream s, byte[] bytes)
    {
        s.Write(bytes);
        s.WriteByte(UnitSep);
    }

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static string FormatTemperature(float? t) =>
        t is null ? "null" : t.Value.ToString("R", CultureInfo.InvariantCulture);

    static string FormatMaxTokens(int? m) =>
        m is null ? "null" : m.Value.ToString(CultureInfo.InvariantCulture);

    static string FormatResponseFormat(ChatResponseFormat? rf) =>
        rf switch
        {
            null => "null",
            ChatResponseFormatJson j when j.Schema is JsonElement schema => schema.GetRawText(),
            ChatResponseFormatJson => "json",
            _ => rf.GetType().Name,
        };

    static string JoinSystemText(IList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            if (m.Role == ChatRole.System)
            {
                sb.Append(m.Text);
                sb.Append(RecordSep);
            }
        }
        return sb.ToString();
    }

    static string JoinNonSystemText(IList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            if (m.Role != ChatRole.System)
            {
                sb.Append(m.Role.Value).Append(GroupSep).Append(m.Text);
                sb.Append(RecordSep);
            }
        }
        return sb.ToString();
    }
}
