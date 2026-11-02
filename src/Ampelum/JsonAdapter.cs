using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Internal;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum;

/// <summary>
/// JSON-schema-based adapter for LM providers that support structured (constrained) output.
/// Formats prompts with an embedded JSON schema and parses responses as JSON objects.
/// Implements <see cref="IStructuredOutputAdapter"/> so <c>Predict</c> automatically sets
/// <c>ChatOptions.ResponseFormat</c> when using this adapter.
/// </summary>
public sealed class JsonAdapter : IAdapter, IStructuredOutputAdapter, ITestableAdapter
{
    readonly bool _strict;
    static readonly ConcurrentDictionary<Type, string> _schemaCache = new();
    static readonly ConcurrentDictionary<Type, ChatResponseFormat> _responseFormatCache = new();

    /// <summary>
    /// Initialises a new <see cref="JsonAdapter"/>.
    /// </summary>
    /// <param name="strict">
    /// When <see langword="true"/> (default), the response must be a bare JSON object.
    /// When <see langword="false"/>, JSON is extracted from surrounding prose using a best-effort span search.
    /// </param>
    public JsonAdapter(bool strict = true) => _strict = strict;

    /// <inheritdoc/>
    public IReadOnlyList<ChatMessage> Format(
        SignatureMetadata signature,
        IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyList<Example> demos)
    {
        // Spec invariant Â§5.8.3: validate inputs and every demo before any serialisation.
        AdapterValidation.ValidateInputs(signature, inputs);

        var schema = _schemaCache.GetOrAdd(signature.SignatureType, _ => BuildSchema(signature));
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(signature.Instructions))
            sb.AppendLine(signature.Instructions).AppendLine();
        sb.AppendLine("Respond with a single JSON object matching this schema:");
        sb.AppendLine(schema);

        var messages = new List<ChatMessage>(2 + demos.Count * 2) { new(ChatRole.System, sb.ToString()) };
        foreach (var demo in demos)
        {
            AdapterValidation.ValidateInputs(signature, demo.Inputs);
            AdapterValidation.ValidateOutputs(signature, demo.Outputs);
            messages.Add(new ChatMessage(ChatRole.User, JsonSerializer.Serialize(demo.Inputs, JsonDefaults.Options)));
            messages.Add(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(demo.Outputs, JsonDefaults.Options)));
        }
        messages.Add(new ChatMessage(ChatRole.User, JsonSerializer.Serialize(inputs, JsonDefaults.Options)));
        return messages;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object?> Parse(SignatureMetadata signature, ChatResponse response)
    {
        if (response.Messages.Count == 0)
            throw new AdapterParseException(signature.SignatureType.Name, nameof(JsonAdapter), "", "response had no messages");

        var text = response.Text;
        if (string.IsNullOrEmpty(text))
            throw new AdapterParseException(
                signature.SignatureType.Name, nameof(JsonAdapter), "",
                "response text was null or empty");

        ReadOnlySpan<char> jsonChars;
        if (_strict)
        {
            jsonChars = text.AsSpan();
        }
        else
        {
            var span = text.AsSpan();
            var s = span.IndexOf('{');
            var e = span.LastIndexOf('}');
            if (s < 0 || e < 0 || e <= s)
                throw new AdapterParseException(
                    signature.SignatureType.Name, nameof(JsonAdapter), text,
                    "no JSON object found in lenient mode");
            jsonChars = span.Slice(s, e - s + 1);
        }

        var maxBytes = checked(jsonChars.Length * 4);
        byte[]? rented = null;
        Span<byte> utf8 = maxBytes <= 1024
            ? stackalloc byte[1024]
            : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(maxBytes));

        try
        {
            var written = Encoding.UTF8.GetBytes(jsonChars, utf8);
            var jsonBytes = utf8[..written];

            var slots = new (int start, int length)[signature.Outputs.Count];
            for (var i = 0; i < slots.Length; i++) slots[i] = (-1, 0);

            try
            {
                ScanRoot(jsonBytes, signature, slots);
            }
            catch (JsonException ex)
            {
                throw new AdapterParseException(
                    signature.SignatureType.Name, nameof(JsonAdapter), text,
                    $"invalid JSON ({ex.Message}); consider lenient mode via new JsonAdapter(strict: false)");
            }

            var outputs = new Dictionary<string, object?>(signature.Outputs.Count);
            for (var i = 0; i < signature.Outputs.Count; i++)
            {
                var f = signature.Outputs[i];
                if (slots[i].start < 0)
                    throw new AdapterParseException(
                        signature.SignatureType.Name, nameof(JsonAdapter), text,
                        $"missing output field '{f.Name}' in JSON response");

                var slice = jsonBytes.Slice(slots[i].start, slots[i].length);
                outputs[f.Name] = JsonSerializer.Deserialize(slice, f.ClrType, JsonDefaults.Options);
            }
            return outputs;
        }
        finally
        {
            if (rented is not null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Walks the top-level object via <see cref="Utf8JsonReader"/>, recording the byte range of
    /// each property whose name (camelCased or PascalCased) matches one of <paramref name="signature"/>'s
    /// outputs. Throws <see cref="JsonException"/> on malformed input.
    /// </summary>
    static void ScanRoot(ReadOnlySpan<byte> jsonBytes, SignatureMetadata signature, (int start, int length)[] slots)
    {
        var reader = new Utf8JsonReader(jsonBytes,
            new JsonReaderOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("root is not a JSON object");

        // Utf8JsonReader.Read() throws JsonException on malformed input; for well-formed
        // input the token sequence after StartObject is (PropertyName, value)* EndObject.
        // The loop exits via the EndObject return; no explicit unterminated-object guard
        // is needed because reader.Read() will surface truncation as a JsonException.
        while (true)
        {
            reader.Read();
            if (reader.TokenType == JsonTokenType.EndObject) return;

            // Utf8JsonReader is constructed from a ReadOnlySpan<byte> here, so HasValueSequence
            // is always false and ValueSpan is the contiguous property-name bytes.
            var nameBytes = reader.ValueSpan.ToArray();
            var matchedIndex = -1;
            for (var i = 0; i < signature.Outputs.Count; i++)
            {
                var fieldName = signature.Outputs[i].Name;
                if (NameMatches(nameBytes, fieldName))
                {
                    matchedIndex = i;
                    break;
                }
            }

            reader.Read();

            if (matchedIndex >= 0 && slots[matchedIndex].start < 0)
            {
                var valueStart = checked((int)reader.TokenStartIndex);
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }
                var valueEnd = checked((int)reader.BytesConsumed);
                slots[matchedIndex] = (valueStart, valueEnd - valueStart);
            }
            else
            {
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    reader.Skip();
            }
        }
    }

    /// <summary>
    /// Matches a UTF-8 JSON property name against a CLR PascalCase field name, accepting either
    /// the exact PascalCase form or the camelCase form (first character lowercased).
    /// </summary>
    static bool NameMatches(byte[] jsonName, string fieldName)
    {
        if (jsonName.Length != fieldName.Length) return false;
        var pascalOk = true;
        for (var i = 0; i < jsonName.Length; i++)
        {
            if (jsonName[i] != (byte)fieldName[i]) { pascalOk = false; break; }
        }
        if (pascalOk) return true;
        if (jsonName[0] != (byte)char.ToLowerInvariant(fieldName[0])) return false;
        for (var i = 1; i < jsonName.Length; i++)
        {
            if (jsonName[i] != (byte)fieldName[i]) return false;
        }
        return true;
    }

    /// <inheritdoc/>
    public ChatResponseFormat BuildResponseFormat(SignatureMetadata signature) =>
        _responseFormatCache.GetOrAdd(signature.SignatureType, _ =>
        {
            var schema = _schemaCache.GetOrAdd(signature.SignatureType, _ => BuildSchema(signature));
            // JsonDocument.Parse allocates a document whose RootElement lifetime is tied to the
            // document. ChatResponseFormat.ForJsonSchema captures the JsonElement by value
            // (it is a struct referencing the document's internal buffer), so we must keep the
            // document alive for the lifetime of the cached format. We intentionally do NOT
            // dispose the document here â€” it lives for the process lifetime alongside the cache.
            var doc = JsonDocument.Parse(schema);
            return ChatResponseFormat.ForJsonSchema(doc.RootElement, signature.SignatureType.Name);
        });

    /// <inheritdoc/>
    public ChatResponse FormatResponse(SignatureMetadata signature, IReadOnlyDictionary<string, object?> outputs)
    {
        var body = new Dictionary<string, object?>(outputs.Count);
        foreach (var f in signature.Outputs)
        {
            if (!outputs.TryGetValue(f.Name, out var v))
                throw new AdapterFormatException($"Missing output '{f.Name}'");
            body[ToCamel(f.Name)] = v;
        }
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(body, JsonDefaults.Options)));
    }

    static string BuildSchema(SignatureMetadata s)
    {
        var props = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var f in s.Outputs)
        {
            props[ToCamel(f.Name)] = new { type = JsonTypeFor(f.ClrType), description = f.Description };
            if (!f.IsOptional)
                required.Add(ToCamel(f.Name));
        }
        return JsonSerializer.Serialize(new
        {
            type = "object",
            properties = props,
            required,
            additionalProperties = false
        }, JsonDefaults.Options);
    }

    static string JsonTypeFor(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (u == typeof(string)) return "string";
        if (u == typeof(bool)) return "boolean";
        if (u == typeof(int) || u == typeof(long) || u == typeof(short)) return "integer";
        if (u == typeof(double) || u == typeof(float)) return "number";
        if (u.IsArray || (u.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(u))) return "array";
        return "string";
    }

    static string ToCamel(string name) =>
        char.ToLowerInvariant(name[0]) + name[1..];

}
