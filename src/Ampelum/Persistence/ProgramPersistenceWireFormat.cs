using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Persistence;

/// <summary>Internal DTO for the JSON wire format (spec Â§7.6.2). Snake-case keys.</summary>
internal sealed class ProgramPersistenceWireFormat
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("tprogram_type")]
    public string TProgramType { get; set; } = string.Empty;

    /// <summary>Wire key "demo_assignments"; maps to C# DemosBySubmodule (PSQ-1).</summary>
    [JsonPropertyName("demo_assignments")]
    public Dictionary<string, List<WireExample>> DemoAssignments { get; set; } = new();

    [JsonPropertyName("instruction_overrides")]
    public Dictionary<string, string> InstructionOverrides { get; set; } = new();
}

/// <summary>Per-example wire shape: separate inputs/outputs maps to round-trip <see cref="Example"/>.</summary>
internal sealed class WireExample
{
    [JsonPropertyName("inputs")]
    public Dictionary<string, object?> Inputs { get; set; } = new();

    [JsonPropertyName("outputs")]
    public Dictionary<string, object?> Outputs { get; set; } = new();
}
