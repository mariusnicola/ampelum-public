using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Audit;

/// <summary>
/// System.Text.Json source-generated context used by the audit hash chain.
/// Property names are alphabetised at serialization time via a SortedDictionary projection
/// (PSQ-1) so byte output is deterministic across .NET versions.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(System.Collections.Generic.SortedDictionary<string, object?>))]
[JsonSerializable(typeof(System.Collections.Generic.IReadOnlyList<object?>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
internal sealed partial class AmpelumAuditJsonContext : JsonSerializerContext
{
}
