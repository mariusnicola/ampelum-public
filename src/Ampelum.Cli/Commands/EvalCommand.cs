using System.CommandLine;
using SignalGrid.Ampelum.Cli.Reporting;

namespace SignalGrid.Ampelum.Cli.Commands;

internal sealed record EvalArgs(string Signature, string Dataset, string Metric, string Output);

internal static class EvalCommand
{
    public static Command Create()
    {
        var sigOpt = new Option<string>("--signature", "-s")
        {
            Required = true,
            Description = "Fully-qualified signature type name (e.g. MyApp.QASignature, MyApp).",
        };
        var dataOpt = new Option<string>("--dataset", "-d")
        {
            Required = true,
            Description = "Path to dataset (.jsonl with {input,expected,predicted} rows).",
        };
        var metricOpt = new Option<string>("--metric", "-m")
        {
            Required = true,
            Description = "Metric name (case-insensitive: exact|f1).",
        };
        var outOpt = new Option<string>("--output", "-o")
        {
            Required = true,
            Description = "Output directory for report.md + report.json.",
        };

        var cmd = new Command("eval", "Run Evaluate<TSig> over a dataset; emit markdown + JSON report.");
        cmd.Options.Add(sigOpt);
        cmd.Options.Add(dataOpt);
        cmd.Options.Add(metricOpt);
        cmd.Options.Add(outOpt);

        cmd.SetAction(async (parse, ct) =>
        {
            var args = new EvalArgs(
                Signature: parse.GetValue(sigOpt)!,
                Dataset: parse.GetValue(dataOpt)!,
                Metric: parse.GetValue(metricOpt)!,
                Output: parse.GetValue(outOpt)!);
            return await EvalRunner.RunAsync(args, parse.Configuration.Output, parse.Configuration.Error, ct)
                .ConfigureAwait(false);
        });

        return cmd;
    }
}
