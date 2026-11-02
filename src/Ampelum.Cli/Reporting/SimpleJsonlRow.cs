using System.Text.Json;

namespace SignalGrid.Ampelum.Cli.Reporting;

/// <summary>Lightweight JSONL parser for {input,expected,predicted} rows.</summary>
internal static class SimpleJsonlRow
{
    public static (string Input, string Expected, string Predicted) Parse(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var input = root.TryGetProperty("input", out var i) ? i.GetString() ?? string.Empty : string.Empty;
        var expected = root.TryGetProperty("expected", out var e) ? e.GetString() ?? string.Empty : string.Empty;
        var predicted = root.TryGetProperty("predicted", out var p) ? p.GetString() ?? string.Empty : string.Empty;
        return (input, expected, predicted);
    }
}
