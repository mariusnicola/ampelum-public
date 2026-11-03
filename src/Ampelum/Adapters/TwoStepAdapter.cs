using System.Text;
using System.Text.Json;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Adapters;

/// <summary>
/// Two-prompt adapter strategy: <see cref="Format"/> mirrors <see cref="ChatAdapter.Format"/>
/// (section-marker prose); <see cref="Parse"/> issues a second LM call via the extraction client
/// to extract the signature's output fields as JSON.
/// </summary>
/// <remarks>
/// <b>Sync-over-async note:</b> <see cref="IAdapter.Parse"/> is synchronous; the extraction call
/// is awaited via <c>.GetAwaiter().GetResult()</c>. Callers SHOULD drive this adapter under
/// <see cref="Predict{TSig}.ForwardAsync"/>, which runs on a thread-pool thread. Introducing an
/// asynchronous adapter contract is deferred to a future breaking change (v0.4+).
/// </remarks>
public sealed class TwoStepAdapter : IAdapter
{
    readonly IChatClient _extractionClient;
    readonly ChatAdapter _inner = new();

    /// <summary>Initializes a new <see cref="TwoStepAdapter"/> with the given extraction LM client.</summary>
    /// <param name="extractionClient">The LM client used to extract output fields as JSON from the primary LM response.</param>
    /// <exception cref="ArgumentNullException"><paramref name="extractionClient"/> is <see langword="null"/>.</exception>
    public TwoStepAdapter(IChatClient extractionClient)
    {
        ArgumentNullException.ThrowIfNull(extractionClient);
        _extractionClient = extractionClient;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ChatMessage> Format(
        SignatureMetadata signature,
        IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyList<Example> demos) =>
        _inner.Format(signature, inputs, demos);

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object?> Parse(SignatureMetadata signature, ChatResponse response)
    {
        var rawText = response.Text ?? "";
        var prompt = BuildExtractionPrompt(signature, rawText);
        ChatResponse extractResponse;
        try
        {
            var ambient = AmpelumContext.Ambient;
#pragma warning disable AMPELUM_V09_PREVIEW
            extractResponse = ambient is null
                ? _extractionClient.GetResponseAsync(prompt).GetAwaiter().GetResult()
                : HookedLmCall.InvokeAsync(ambient with { Chat = _extractionClient }, "TwoStepAdapter.Extract", prompt)
                    .GetAwaiter().GetResult();
#pragma warning restore AMPELUM_V09_PREVIEW
        }
        catch (Exception ex)
        {
            throw new AdapterParseException(signature.SignatureType.Name, nameof(TwoStepAdapter), rawText,
                $"extraction call failed: {ex.Message}");
        }

        var jsonText = extractResponse.Text ?? "";
        var result = new Dictionary<string, object?>(signature.Outputs.Count);
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            foreach (var f in signature.Outputs)
            {
                if (doc.RootElement.TryGetProperty(f.Name, out var prop))
                    result[f.Name] = ConvertJson(prop, f.ClrType);
                else
                    result[f.Name] = null;
            }
        }
        catch (JsonException ex)
        {
            throw new AdapterParseException(signature.SignatureType.Name, nameof(TwoStepAdapter), rawText,
                $"extraction produced non-JSON response: {ex.Message}");
        }
        return result;
    }

    static List<ChatMessage> BuildExtractionPrompt(SignatureMetadata signature, string rawText)
    {
        var sb = new StringBuilder();
        sb.Append("Extract the following fields from the text below as a JSON object: {");
        for (var i = 0; i < signature.Outputs.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var f = signature.Outputs[i];
            sb.Append(f.Name).Append(": ").Append(f.ClrType.Name);
        }
        sb.AppendLine("}.");
        sb.Append("Text: ").AppendLine(rawText);
        return new List<ChatMessage> { new(ChatRole.User, sb.ToString()) };
    }

    static object? ConvertJson(JsonElement el, Type t)
    {
        if (t == typeof(string)) return el.GetString();
        if (t == typeof(int)) return el.GetInt32();
        if (t == typeof(long)) return el.GetInt64();
        if (t == typeof(double)) return el.GetDouble();
        if (t == typeof(bool)) return el.GetBoolean();
        return el.GetRawText();
    }
}
