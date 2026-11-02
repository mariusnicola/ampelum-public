using System.CommandLine;
using System.Reflection;

namespace SignalGrid.Ampelum.Cli.Commands;

internal static class RootCommandFactory
{
    public static CommandLineConfiguration Create()
        => Create(new DefaultProcessRunner());

    internal static CommandLineConfiguration Create(IProcessRunner runner)
    {
        var root = new RootCommand("ampelum - Ampelum CLI: scaffolding, benchmarks, evaluation, REPL.");

        root.Subcommands.Add(NewCommand.Create(runner));
        root.Subcommands.Add(BenchCommand.Create());
        root.Subcommands.Add(EvalCommand.Create());
        root.Subcommands.Add(ReplCommand.Create());

        return new CommandLineConfiguration(root);
    }

    internal static string ResolveVersion()
    {
        var asm = typeof(RootCommandFactory).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        var ver = asm.GetName().Version ?? new Version(0, 0, 0);
        return $"{ver.Major}.{ver.Minor}.{ver.Build}";
    }
}
