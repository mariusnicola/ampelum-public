using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Hooks;
using Microsoft.Extensions.DependencyInjection;
using SignalGrid.Ampelum.Host.Tools;

[assembly: ExcludeFromCodeCoverage(Justification = "Bootstrap exercised by E2E only; spec §13.")]

namespace SignalGrid.Ampelum.Host;

internal sealed class Program
{
    private Program() { }

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "rebuild-index")
            return await HostCli.RunAsync(args, default);

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddAmpelum()
            .AddAuditHook();              // registers InMemoryAuditSink + AuditHook together
        // BudgetHook: no global budget in the stub host; real deployments supply IBudget via options.
        builder.Services.AddSingleton<IHook>(_ => new BudgetHook(null));
        builder.Services.AddAmpelumHost();
        builder.Services.AddOpenApi();
        var app = builder.Build();
        // Real consumers call app.MapAmpelumModule<TSig>(...) here before MapAmpelumHostEndpoints.
        app.MapAmpelumHostEndpoints();
        await app.RunAsync();
        return 0;
    }
}
