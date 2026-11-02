using System.Text.Json;

namespace SignalGrid.Ampelum.Host.Internal;

internal static class JsonElementUnboxer
{
    /// <summary>Converts a wire dict of JsonElements to in-process object? values for Predict forward pass.</summary>
    public static IReadOnlyDictionary<string, object?> Unbox(IReadOnlyDictionary<string, JsonElement> wire)
    {
        var result = new Dictionary<string, object?>(wire.Count);
        foreach (var (k, v) in wire)
            result[k] = UnboxElement(v);
        return result;
    }

    static object? UnboxElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String  => el.GetString(),
        JsonValueKind.Number  => el.TryGetInt64(out var i) ? i : el.GetDouble(),
        JsonValueKind.True    => true,
        JsonValueKind.False   => false,
        JsonValueKind.Null    => null,
        JsonValueKind.Object  => UnboxObject(el),
        JsonValueKind.Array   => UnboxArray(el),
        _                     => throw new JsonException($"Cannot unbox JsonValueKind.{el.ValueKind}"),
    };

    static IReadOnlyDictionary<string, object?> UnboxObject(JsonElement el)
    {
        var d = new Dictionary<string, object?>();
        foreach (var prop in el.EnumerateObject())
            d[prop.Name] = UnboxElement(prop.Value);
        return d;
    }

    static IReadOnlyList<object?> UnboxArray(JsonElement el)
    {
        var list = new List<object?>(el.GetArrayLength());
        foreach (var item in el.EnumerateArray())
            list.Add(UnboxElement(item));
        return list;
    }

    /// <summary>
    /// Converts in-process object? values back to a wire dict of JsonElements.
    /// </summary>
    public static IReadOnlyDictionary<string, JsonElement> Box(IReadOnlyDictionary<string, object?> values)
    {
        var result = new Dictionary<string, JsonElement>(values.Count);
        foreach (var (k, v) in values)
            result[k] = BoxValue(v);
        return result;
    }

    static JsonElement BoxValue(object? v) => v switch
    {
        null => JsonSerializer.SerializeToElement((object?)null),
        string s => JsonSerializer.SerializeToElement(s),
        long l => JsonSerializer.SerializeToElement(l),
        int i => JsonSerializer.SerializeToElement(i),
        double d => JsonSerializer.SerializeToElement(d),
        float f => JsonSerializer.SerializeToElement(f),
        bool b => JsonSerializer.SerializeToElement(b),
        IReadOnlyDictionary<string, object?> dict => BoxDict(dict),
        IReadOnlyList<object?> list => BoxList(list),
        _ => JsonSerializer.SerializeToElement(v),
    };

    static JsonElement BoxDict(IReadOnlyDictionary<string, object?> dict)
    {
        using var ms = new System.IO.MemoryStream();
        using var w = new Utf8JsonWriter(ms);
        w.WriteStartObject();
        foreach (var (k, v) in dict)
        {
            w.WritePropertyName(k);
            WriteValue(w, v);
        }
        w.WriteEndObject();
        w.Flush();
        ms.Position = 0;
        return JsonDocument.Parse(ms).RootElement.Clone();
    }

    static JsonElement BoxList(IReadOnlyList<object?> list)
    {
        using var ms = new System.IO.MemoryStream();
        using var w = new Utf8JsonWriter(ms);
        w.WriteStartArray();
        foreach (var v in list)
            WriteValue(w, v);
        w.WriteEndArray();
        w.Flush();
        ms.Position = 0;
        return JsonDocument.Parse(ms).RootElement.Clone();
    }

    static void WriteValue(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null:
                w.WriteNullValue();
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            case long l:
                w.WriteNumberValue(l);
                break;
            case int i:
                w.WriteNumberValue(i);
                break;
            case double d:
                w.WriteNumberValue(d);
                break;
            case float f:
                w.WriteNumberValue(f);
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case IReadOnlyDictionary<string, object?> dict:
                w.WriteStartObject();
                foreach (var (k, val) in dict)
                {
                    w.WritePropertyName(k);
                    WriteValue(w, val);
                }
                w.WriteEndObject();
                break;
            case IReadOnlyList<object?> list:
                w.WriteStartArray();
                foreach (var item in list)
                    WriteValue(w, item);
                w.WriteEndArray();
                break;
            default:
                var el = JsonSerializer.SerializeToElement(v);
                el.WriteTo(w);
                break;
        }
    }
}
