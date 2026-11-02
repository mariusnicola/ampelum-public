using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Context.Selection;

/// <summary>STJ source-gen context for selection plan types.</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SelectionPlan))]
[JsonSerializable(typeof(SelectionRequest))]
[JsonSerializable(typeof(ContextPrefix))]
internal partial class SelectionJsonContext : JsonSerializerContext { }
