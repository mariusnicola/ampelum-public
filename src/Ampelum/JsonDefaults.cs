using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SignalGrid.Ampelum;

/// <summary>
/// Provides the shared, frozen <see cref="JsonSerializerOptions"/> used throughout Ampelum
/// for consistent camelCase serialization, null-value omission, and case-insensitive deserialization.
/// </summary>
public static class JsonDefaults
{
    /// <summary>
    /// Read-only (frozen) <see cref="JsonSerializerOptions"/> shared by all Ampelum adapters.
    /// Do not mutate; the instance is sealed at startup.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = Build();

    static JsonSerializerOptions Build()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        o.MakeReadOnly();
        return o;
    }
}
