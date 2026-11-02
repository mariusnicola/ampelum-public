using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SignalGrid.Ampelum.ProviderAdapters;

/// <summary>
/// Pure converter that round-trips <see cref="NormalizedToolCall"/> instances across the
/// three dominant tool-call shapes â€” OpenAI, Anthropic and Gemini (PSQ-7, v0.9g).
/// </summary>
/// <remarks>
/// All conversions are total â€” empty argument bags, nested objects, parallel multi-call
/// payloads and Gemini-style id-less calls are all handled. Synthetic Gemini ids use the
/// pattern <c>gemini-tool-{index}-{8hex}</c> so cross-provider equality compares only on
/// <see cref="NormalizedToolCall.Name"/> and <see cref="NormalizedToolCall.Arguments"/>.
/// </remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class ToolCallFormatConverter
{
    /// <summary>Parses an OpenAI <c>message.tool_calls</c> JSON array into normalized form.</summary>
    /// <param name="openAiToolCallsJson">The JSON text of the <c>tool_calls</c> array.</param>
    /// <returns>One <see cref="NormalizedToolCall"/> per element.</returns>
    public static IReadOnlyList<NormalizedToolCall> FromOpenAi(string openAiToolCallsJson)
    {
        ArgumentNullException.ThrowIfNull(openAiToolCallsJson);
        using var doc = JsonDocument.Parse(openAiToolCallsJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Expected a JSON array.", nameof(openAiToolCallsJson));

        var result = new List<NormalizedToolCall>(root.GetArrayLength());
        foreach (var element in root.EnumerateArray())
        {
            var id = element.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()! : "";
            var fn = element.GetProperty("function");
            var name = fn.GetProperty("name").GetString() ?? "";
            var argsJson = fn.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String
                ? argsEl.GetString() ?? "{}"
                : "{}";
            result.Add(new NormalizedToolCall(id, name, ParseArgsObject(argsJson)));
        }
        return result;
    }

    /// <summary>Renders normalized calls into an OpenAI-shaped <c>tool_calls</c> JSON array.</summary>
    public static string ToOpenAi(IReadOnlyList<NormalizedToolCall> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        var arr = new JsonArray();
        foreach (var c in calls)
        {
            arr.Add(new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = c.Name,
                    ["arguments"] = ArgsToJsonString(c.Arguments),
                },
            });
        }
        return arr.ToJsonString();
    }

    /// <summary>Parses Anthropic <c>content[]</c> blocks (filters to <c>tool_use</c>).</summary>
    public static IReadOnlyList<NormalizedToolCall> FromAnthropic(string anthropicContentJson)
    {
        ArgumentNullException.ThrowIfNull(anthropicContentJson);
        using var doc = JsonDocument.Parse(anthropicContentJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Expected a JSON array.", nameof(anthropicContentJson));

        var result = new List<NormalizedToolCall>();
        foreach (var block in root.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var typeEl)
                || typeEl.GetString() != "tool_use") continue;
            var id = block.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()! : "";
            var name = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
            IReadOnlyDictionary<string, object?> args;
            if (block.TryGetProperty("input", out var inputEl) && inputEl.ValueKind == JsonValueKind.Object)
                args = ParseObjectElement(inputEl);
            else
                args = new Dictionary<string, object?>();
            result.Add(new NormalizedToolCall(id, name, args));
        }
        return result;
    }

    /// <summary>Renders normalized calls into Anthropic-shaped <c>content[]</c> JSON.</summary>
    public static string ToAnthropic(IReadOnlyList<NormalizedToolCall> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        var arr = new JsonArray();
        foreach (var c in calls)
        {
            arr.Add(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = c.Id,
                ["name"] = c.Name,
                ["input"] = ArgsToJsonNode(c.Arguments),
            });
        }
        return arr.ToJsonString();
    }

    /// <summary>Parses Gemini <c>parts[]</c> JSON (filters to entries with <c>functionCall</c>).</summary>
    public static IReadOnlyList<NormalizedToolCall> FromGemini(string geminiPartsJson)
    {
        ArgumentNullException.ThrowIfNull(geminiPartsJson);
        using var doc = JsonDocument.Parse(geminiPartsJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Expected a JSON array.", nameof(geminiPartsJson));

        var result = new List<NormalizedToolCall>();
        var index = 0;
        foreach (var part in root.EnumerateArray())
        {
            if (!part.TryGetProperty("functionCall", out var fc)) continue;
            var name = fc.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
            IReadOnlyDictionary<string, object?> args;
            if (fc.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
                args = ParseObjectElement(argsEl);
            else
                args = new Dictionary<string, object?>();
            var id = $"gemini-tool-{index}-{Guid.NewGuid().ToString("N")[..8]}";
            result.Add(new NormalizedToolCall(id, name, args));
            index++;
        }
        return result;
    }

    /// <summary>Renders normalized calls into Gemini-shaped <c>parts[]</c> JSON.</summary>
    public static string ToGemini(IReadOnlyList<NormalizedToolCall> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        var arr = new JsonArray();
        foreach (var c in calls)
        {
            arr.Add(new JsonObject
            {
                ["functionCall"] = new JsonObject
                {
                    ["name"] = c.Name,
                    ["args"] = ArgsToJsonNode(c.Arguments),
                },
            });
        }
        return arr.ToJsonString();
    }

    static IReadOnlyDictionary<string, object?> ParseArgsObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, object?>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return new Dictionary<string, object?>();
        return ParseObjectElement(doc.RootElement);
    }

    static IReadOnlyDictionary<string, object?> ParseObjectElement(JsonElement obj)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
            dict[prop.Name] = ConvertElement(prop.Value);
        return dict;
    }

    static object? ConvertElement(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null: return null;
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.String: return el.GetString();
            case JsonValueKind.Number:
                if (el.TryGetInt64(out var l)) return l;
                return el.GetDouble();
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in el.EnumerateArray()) list.Add(ConvertElement(item));
                return list;
            default:
                return ParseObjectElement(el);
        }
    }

    static string ArgsToJsonString(IReadOnlyDictionary<string, object?> args)
    {
        var node = ArgsToJsonNode(args);
        return node.ToJsonString();
    }

    static JsonNode ArgsToJsonNode(IReadOnlyDictionary<string, object?> args)
    {
        var obj = new JsonObject();
        foreach (var kv in args) obj[kv.Key] = ToJsonNode(kv.Value);
        return obj;
    }

    static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create(i),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create(f),
        decimal m => JsonValue.Create(m),
        IReadOnlyDictionary<string, object?> dict => ArgsToJsonNode(dict),
        IEnumerable<object?> arr => ArrayToJsonNode(arr),
        _ => JsonValue.Create(value.ToString()),
    };

    static JsonArray ArrayToJsonNode(IEnumerable<object?> items)
    {
        var arr = new JsonArray();
        foreach (var item in items) arr.Add(ToJsonNode(item));
        return arr;
    }
}
