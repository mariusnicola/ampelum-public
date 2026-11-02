using SignalGrid.Ampelum.Cli.Commands;

namespace SignalGrid.Ampelum.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var config = RootCommandFactory.Create();
        return await config.InvokeAsync(args).ConfigureAwait(false);
    }
}
