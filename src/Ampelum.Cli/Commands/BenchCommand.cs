using System.CommandLine;

namespace SignalGrid.Ampelum.Cli.Commands;

internal static class BenchCommand
{
    public static Command Create()
    {
        var sigOpt = new Option<string>("--signature", "-s")
        {
            Required = true,
            Description = "Signature type name (used as <Sig>Bench class name in the generated harness).",
        };
        var outOpt = new Option<string>("--output", "-o")
        {
            Required = true,
            Description = "Output directory for the generated BenchmarkDotNet harness.",
        };

        var cmd = new Command("bench", "Scaffold a BenchmarkDotNet harness against a signature.");
        cmd.Options.Add(sigOpt);
        cmd.Options.Add(outOpt);

        cmd.SetAction(parse =>
        {
            var sig = parse.GetValue(sigOpt)!;
            var outDir = parse.GetValue(outOpt)!;
            BenchHarnessScaffolder.Write(sig, outDir);
            parse.Configuration.Output.WriteLine(
                $"Wrote BenchmarkDotNet harness for {sig} to {outDir}.");
            return 0;
        });

        return cmd;
    }
}
