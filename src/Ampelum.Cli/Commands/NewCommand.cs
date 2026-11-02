using System.CommandLine;

namespace SignalGrid.Ampelum.Cli.Commands;

internal static class NewCommand
{
    internal static readonly IReadOnlySet<string> AllowedTemplates = new HashSet<string>(StringComparer.Ordinal)
    {
        "ampelum-backend",
        "ampelum-researcher",
        "ampelum-enterprise",
        "ampelum-platform",
        "ampelum-agent",
    };

    public static Command Create(IProcessRunner runner)
    {
        var template = new Argument<string>("template")
        {
            Description = "Template short-name (ampelum-backend|researcher|enterprise|platform|agent).",
        };
        var name = new Option<string?>("--name", "-n")
        {
            Description = "Project / output assembly name (passed to dotnet new).",
        };
        var output = new Option<string?>("--output", "-o")
        {
            Description = "Output directory (passed to dotnet new).",
        };

        var cmd = new Command("new", "Scaffold a new Ampelum project from a persona template.");
        cmd.Arguments.Add(template);
        cmd.Options.Add(name);
        cmd.Options.Add(output);

        cmd.SetAction(async (parse, ct) =>
        {
            var t = parse.GetValue(template) ?? string.Empty;
            if (!AllowedTemplates.Contains(t))
            {
                parse.Configuration.Error.WriteLine(
                    $"Unknown template '{t}'. Expected one of: {string.Join(", ", AllowedTemplates)}.");
                return 2;
            }

            var args = new List<string> { "new", t };
            var nm = parse.GetValue(name);
            if (!string.IsNullOrWhiteSpace(nm))
            {
                args.Add("--name");
                args.Add(nm);
            }
            var outp = parse.GetValue(output);
            if (!string.IsNullOrWhiteSpace(outp))
            {
                args.Add("--output");
                args.Add(outp);
            }

            return await runner.RunAsync("dotnet", args, ct).ConfigureAwait(false);
        });

        return cmd;
    }
}
