using System.Text.Json;

namespace SignalGrid.Ampelum.Cli.Reporting;

internal static class JsonReportWriter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static async Task WriteAsync(EvalReport report, string path, CancellationToken ct)
    {
        await using var fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, report, Options, ct).ConfigureAwait(false);
    }
}
