using System.Text;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Internal;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum;

/// <summary>
/// Text-based adapter that formats prompts using <c>[[ ## FieldName ## ]]</c> section markers and
/// parses LM responses by splitting on the same markers. Compatible with any chat model; no JSON schema required.
/// Use <see cref="JsonAdapter"/> when the LM provider supports structured (constrained) output.
/// </summary>
public sealed class ChatAdapter : IAdapter, ITestableAdapter, IStreamingAdapter
{
    /// <inheritdoc/>
    public IReadOnlyList<ChatMessage> Format(
        SignatureMetadata signature,
        IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyList<Example> demos)
    {
        AdapterValidation.ValidateInputs(signature, inputs);
        // v0.9a: image-generation OUTPUT (byte[]/Uri) is not supported in this milestone.
        foreach (var f in signature.Outputs)
        {
            if (IsBinaryOrUri(f.ClrType))
                throw new NotSupportedException(
                    $"image generation output not supported in v0.9a (output field '{f.Name}' has type {f.ClrType.Name}).");
        }

        var messages = new List<ChatMessage>(2 + demos.Count * 2) { new(ChatRole.System, BuildSystem(signature)) };
        foreach (var demo in demos)
        {
            AdapterValidation.ValidateInputs(signature, demo.Inputs);
            AdapterValidation.ValidateOutputs(signature, demo.Outputs);
            messages.Add(new ChatMessage(ChatRole.User, RenderFields(signature.Inputs, demo.Inputs)));
            messages.Add(new ChatMessage(ChatRole.Assistant, RenderFields(signature.Outputs, demo.Outputs)));
        }
        messages.Add(BuildUserMessage(signature, inputs));
        return messages;
    }

    static bool IsBinaryOrUri(Type t) =>
        t == typeof(byte[]) || t == typeof(Uri);

    static ChatMessage BuildUserMessage(SignatureMetadata signature, IReadOnlyDictionary<string, object?> inputs)
    {
        // Gather binary / Uri input fields and route them to MEAI multi-modal content parts.
        // Text fields render via the normal ChatAdapter marker block.
        var hasBinary = signature.Inputs.Any(f => IsBinaryOrUri(f.ClrType) && inputs.TryGetValue(f.Name, out var v) && v is not null);
        var textFields = signature.Inputs.Where(f => !IsBinaryOrUri(f.ClrType)).ToList();
        var textValues = new Dictionary<string, object?>(textFields.Count);
        foreach (var f in textFields) textValues[f.Name] = inputs[f.Name];
        var text = RenderFields(textFields, textValues);

        if (!hasBinary)
            return new ChatMessage(ChatRole.User, text);

        var msg = new ChatMessage(ChatRole.User, contents: []);
        if (!string.IsNullOrEmpty(text))
            msg.Contents.Add(new TextContent(text));
        foreach (var f in signature.Inputs)
        {
            if (!IsBinaryOrUri(f.ClrType)) continue;
            if (!inputs.TryGetValue(f.Name, out var raw) || raw is null) continue;
            switch (raw)
            {
                case byte[] bytes:
                    msg.Contents.Add(new DataContent(bytes, mediaType: "image/png"));
                    break;
                case Uri uri:
                    msg.Contents.Add(new UriContent(uri, mediaType: "image/*"));
                    break;
            }
        }
        return msg;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object?> Parse(SignatureMetadata signature, ChatResponse response)
    {
        var text = ExtractText(response)
            ?? throw new AdapterParseException(signature.SignatureType.Name, nameof(ChatAdapter), "", "response had no text content");

        var sections = SplitSections(text);
        var outputs = new Dictionary<string, object?>(signature.Outputs.Count);
        foreach (var field in signature.Outputs)
        {
            if (!sections.TryGetValue(field.Name, out var raw))
                throw new AdapterParseException(
                    signature.SignatureType.Name,
                    nameof(ChatAdapter),
                    text,
                    $"missing section '[[ ## {field.Name} ## ]]' â€” try JsonAdapter for structured-output providers");
            outputs[field.Name] = ConvertTo(raw.Trim(), field.ClrType);
        }
        return outputs;
    }

    /// <inheritdoc/>
    public ChatResponse FormatResponse(SignatureMetadata signature, IReadOnlyDictionary<string, object?> outputs)
    {
        AdapterValidation.ValidateOutputs(signature, outputs);
        var text = RenderFields(signature.Outputs, outputs);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
    }

    static string BuildSystem(SignatureMetadata s)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(s.Instructions)) sb.AppendLine(s.Instructions).AppendLine();
        sb.AppendLine("You will receive input fields and must reply using the given output field markers.");
        sb.AppendLine("Input fields:");
        foreach (var f in s.Inputs)  sb.AppendLine($"- [[ ## {f.Name} ## ]] ({f.Description})");
        sb.AppendLine("Output fields:");
        foreach (var f in s.Outputs) sb.AppendLine($"- [[ ## {f.Name} ## ]] ({f.Description})");
        return sb.ToString();
    }

    static string RenderFields(IReadOnlyList<Field> fields, IReadOnlyDictionary<string, object?> values)
    {
        var sb = new StringBuilder();
        foreach (var f in fields)
        {
            sb.Append("[[ ## ").Append(f.Name).Append(" ## ]]\n");
            sb.AppendLine(Convert.ToString(values[f.Name], System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Span-based section splitter â€” equivalent to the prior <c>Split('\n')</c> + <c>TrimEnd('\r')</c>
    /// + per-line <c>StartsWith</c>/<c>EndsWith</c> implementation, but scans <paramref name="text"/>
    /// once via <see cref="ReadOnlySpan{Char}"/> indexing with no intermediate string allocations
    /// for the line array or per-line trim copies.
    /// </summary>
    static IReadOnlyDictionary<string, string> SplitSections(string text)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        var span = text.AsSpan();
        string? currentName = null;
        var body = new StringBuilder();

        var i = 0;
        while (i <= span.Length)
        {
            var rest = span[i..];
            var lineEnd = rest.IndexOf('\n');
            var nextI = lineEnd < 0 ? span.Length : i + lineEnd;

            var lineEndTrim = nextI > i && span[nextI - 1] == '\r' ? nextI - 1 : nextI;
            var line = span[i..lineEndTrim];

            if (TryMatchMarker(line, out var nameStart, out var nameLen, out var inlineStart))
            {
                if (currentName is not null) sections[currentName] = body.ToString().TrimEnd();
                currentName = new string(line.Slice(nameStart, nameLen));
                body.Clear();

                // Tolerate the value appearing inline on the same line as its marker
                // (e.g. "[[ ## Sentiment ## ]] Positive") in addition to the standard
                // next-line format some providers don't reproduce reliably.
                var inline = line[inlineStart..];
                var k = 0;
                while (k < inline.Length && IsAsciiWs(inline[k])) k++;
                var inlineContent = inline[k..];
                if (inlineContent.Length > 0)
                {
                    body.Append(inlineContent);
                    body.Append('\n');
                }
            }
            else if (currentName is not null)
            {
                body.Append(line);
                body.Append('\n');
            }

            if (lineEnd < 0) break;
            i = nextI + 1;
        }

        if (currentName is not null) sections[currentName] = body.ToString().TrimEnd();
        return sections;
    }

    /// <summary>
    /// Span-based marker matcher: returns true iff <paramref name="line"/> (after skipping
    /// leading whitespace) starts with a well-formed <c>[[ ## NAME ## ]]</c> marker, and emits
    /// NAME's offset+length within <paramref name="line"/> plus <paramref name="inlineStart"/>,
    /// the offset immediately after the closing <c>]]</c> where an (optional) same-line value
    /// begins. Some providers emit the value inline (<c>[[ ## Sentiment ## ]] Positive</c>)
    /// instead of on the following line; the caller decides whether trailing content
    /// there is present and non-whitespace.
    /// </summary>
    static bool TryMatchMarker(ReadOnlySpan<char> line, out int nameStart, out int nameLen, out int inlineStart)
    {
        nameStart = nameLen = inlineStart = 0;

        var start = 0;
        while (start < line.Length && IsAsciiWs(line[start])) start++;
        var rest = line[start..];

        if (rest.Length < 8) return false;
        if (rest[0] != '[' || rest[1] != '[') return false;

        var closeRel = rest[2..].IndexOf("]]");
        if (closeRel < 0) return false;
        var closeIdx = 2 + closeRel; // index of the first ']' of the closing "]]" within `rest`

        var inner = rest[2..closeIdx];
        var innerStart = 0;
        var innerEnd = inner.Length;
        while (innerStart < innerEnd && IsAsciiWs(inner[innerStart])) innerStart++;
        while (innerEnd > innerStart && IsAsciiWs(inner[innerEnd - 1])) innerEnd--;
        if (innerEnd - innerStart < 4) return false;

        var t = inner[innerStart..innerEnd];
        if (t[0] != '#' || t[1] != '#' || t[^1] != '#' || t[^2] != '#') return false;

        var hashEndStart = 2;
        var hashEndEnd = t.Length - 2;
        while (hashEndStart < hashEndEnd && IsAsciiWs(t[hashEndStart])) hashEndStart++;
        while (hashEndEnd > hashEndStart && IsAsciiWs(t[hashEndEnd - 1])) hashEndEnd--;
        if (hashEndEnd <= hashEndStart) return false;

        nameStart = start + 2 + innerStart + hashEndStart;
        nameLen = hashEndEnd - hashEndStart;
        inlineStart = start + closeIdx + 2; // just past the closing "]]"
        return true;
    }

    static bool IsAsciiWs(char c) => c == ' ' || c == '\t' || c == '\r' || c == '\n';

    static string? ExtractText(ChatResponse response)
    {
        if (response.Messages.Count == 0) return null;
        return response.Text;
    }

    static object? ConvertTo(string raw, Type type)
    {
        var u = Nullable.GetUnderlyingType(type) ?? type;
        if (u == typeof(string)) return raw;
        if (u.IsEnum) return Enum.Parse(u, raw, ignoreCase: true);
        if (u == typeof(Guid)) return Guid.Parse(raw);
        if (u == typeof(DateTime)) return DateTime.Parse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        if (u == typeof(DateTimeOffset)) return DateTimeOffset.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        return System.Text.Json.JsonSerializer.Deserialize(raw, u, JsonDefaults.Options);
    }

    // â”€â”€ IStreamingAdapter â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <inheritdoc/>
    public StreamingParseState OnChunk(SignatureMetadata signature, StreamingParseState state, ChatResponseUpdate chunk)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(chunk);

        var incoming = chunk.Text;
        if (string.IsNullOrEmpty(incoming)) return state;

        var buffer = state.PendingTail + incoming;
        var (committed, curField, tail, completed) = ProcessBuffer(buffer, state.CurrentFieldName, state.CompletedFields);

        return state with
        {
            CurrentFieldName = curField,
            AccumulatedText = state.AccumulatedText + committed,
            PendingTail = tail,
            CompletedFields = completed,
        };
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object?> Finalize(SignatureMetadata signature, StreamingParseState state)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(state);

        // Build a mutable working copy of completed fields (raw strings).
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in state.CompletedFields)
            raw[kv.Key] = kv.Value is string s ? s : "";

        // Flush pending tail as literal text for the current field.
        if (!string.IsNullOrEmpty(state.CurrentFieldName))
        {
            raw.TryGetValue(state.CurrentFieldName, out var prior);
            raw[state.CurrentFieldName] = (prior ?? "") + state.PendingTail;
        }

        // Type-convert every declared output; throw if any is missing.
        var final = new Dictionary<string, object?>(signature.Outputs.Count);
        foreach (var field in signature.Outputs)
        {
            if (!raw.TryGetValue(field.Name, out var fieldRaw))
                throw new AdapterParseException(
                    signature.SignatureType.Name,
                    nameof(ChatAdapter),
                    state.AccumulatedText,
                    $"missing streaming output field '{field.Name}'");

            final[field.Name] = ConvertTo(fieldRaw.Trim(), field.ClrType);
        }
        return final;
    }

    /// <summary>
    /// Scans <paramref name="buffer"/> for complete <c>[[ ## NAME ## ]]</c> markers.
    /// Returns the committed text (for observability), the current field name after scanning,
    /// the pending tail (possible partial marker), and an updated completed-fields map.
    /// <see cref="StreamingParseState.MaxPartialMarkerLength"/> caps the tail length.
    /// </summary>
    static (string committed, string curField, string tail, IReadOnlyDictionary<string, object?>) ProcessBuffer(
        string buffer,
        string curField,
        IReadOnlyDictionary<string, object?> completedFields)
    {
        // Work copy so we can accumulate per-field raw text.
        var work = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in completedFields)
            work[kv.Key] = kv.Value is string s ? s : "";

        var segStart = 0;
        var sb = new StringBuilder(); // accumulates committed text for observability

        while (true)
        {
            var markerStart = buffer.IndexOf("[[ ## ", segStart, StringComparison.Ordinal);
            if (markerStart < 0) break;

            var nameStart = markerStart + 6; // skip "[[ ## "
            var markerEnd = buffer.IndexOf(" ## ]]", nameStart, StringComparison.Ordinal);
            if (markerEnd < 0) break; // incomplete marker â€” will become tail

            var name = buffer[nameStart..markerEnd].Trim();
            if (name.Length == 0)
            {
                // Malformed (empty name) â€” skip past this marker and keep scanning.
                segStart = markerEnd + 6;
                continue;
            }

            // Commit text from segStart to markerStart into the current field.
            if (markerStart > segStart && !string.IsNullOrEmpty(curField))
            {
                var pre = buffer[segStart..markerStart];
                work.TryGetValue(curField, out var existing);
                work[curField] = (existing ?? "") + pre;
                sb.Append(pre);
            }
            else if (markerStart > segStart)
            {
                // Pre-marker junk with no current field â€” discard (mirrors SplitSections).
                sb.Append(buffer[segStart..markerStart]);
            }

            curField = name;
            segStart = markerEnd + 6; // skip " ## ]]"
        }

        // Remaining buffer after last complete marker: split into committed + tail.
        var remaining = buffer[segStart..];
        var tailLen = FindLongestMarkerPrefix(remaining);
        var committed = remaining[..^tailLen];
        var tail = remaining[^tailLen..];

        if (committed.Length > 0 && !string.IsNullOrEmpty(curField))
        {
            work.TryGetValue(curField, out var existing);
            work[curField] = (existing ?? "") + committed;
        }
        sb.Append(committed);

        // Build IReadOnlyDictionary from work (values as raw strings boxed as object?).
        var completed = new Dictionary<string, object?>(work.Count, StringComparer.Ordinal);
        foreach (var kv in work) completed[kv.Key] = kv.Value;

        return (sb.ToString(), curField, tail, completed);
    }

    /// <summary>
    /// Returns the length of the longest suffix of <paramref name="text"/> that is a valid prefix
    /// of the pattern <c>[[ ## NAME ## ]]</c>, capped at <see cref="StreamingParseState.MaxPartialMarkerLength"/>.
    /// Returns 0 if no suffix qualifies.
    /// </summary>
    static int FindLongestMarkerPrefix(string text)
    {
        var maxCheck = Math.Min(StreamingParseState.MaxPartialMarkerLength, text.Length);
        for (var k = maxCheck; k >= 1; k--)
        {
            if (IsMarkerPrefix(text[(text.Length - k)..])) return k;
        }
        return 0;
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="s"/> is a valid non-empty prefix of
    /// <c>[[ ## NAME ## ]]</c>, meaning the stream could continue and complete a marker.
    /// </summary>
    static bool IsMarkerPrefix(string s)
    {
        // The full marker is: "[[ ## " + NAME + " ## ]]"
        ReadOnlySpan<char> sp = s;
        ReadOnlySpan<char> marker = "[[ ## ";

        // Phase 1: building up the fixed "[[ ## " prefix (6 chars).
        if (sp.Length <= 6)
            return marker.StartsWith(sp, StringComparison.Ordinal);

        // Phase 2: "[[ ## " is present â€” rest = NAME_partial + optional_suffix_of_" ## ]]".
        if (!sp.StartsWith(marker, StringComparison.Ordinal)) return false;

        var rest = sp[6..];
        ReadOnlySpan<char> term = " ## ]]";

        // Find the longest suffix of rest that is a prefix of " ## ]]".
        for (var k = Math.Min(term.Length, rest.Length); k >= 1; k--)
        {
            if (term.StartsWith(rest[^k..], StringComparison.Ordinal))
            {
                var namePart = rest[..^k];
                // Suffix matched the terminator; name part (if any) must be valid.
                return namePart.IsEmpty || IsValidNameChars(namePart);
            }
        }

        // No suffix of rest matches the terminator â€” rest must be entirely NAME chars.
        return IsValidNameChars(rest);
    }

    static bool IsValidNameChars(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
        {
            if (c == '[') return false;
            if (c == ']') return false;
            if (c == '#') return false;
        }
        return true;
    }
}
