namespace SignalGrid.Ampelum.Cli.Reporting;

internal sealed record EvalRow(int Index, string Input, string Expected, string Predicted, double Score);

internal sealed record EvalReport(
    string Signature,
    string Metric,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<EvalRow> Rows,
    double MeanScore);
