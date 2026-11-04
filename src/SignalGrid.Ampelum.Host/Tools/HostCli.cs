using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SignalGrid.Ampelum.Host.ModuleRegistry;
using SignalGrid.Ampelum.Host.Persistence;

namespace SignalGrid.Ampelum.Host.Tools;

/// <summary>CLI dispatcher for host admin commands.</summary>
[ExcludeFromCodeCoverage]
internal static class HostCli
{
    /// <summary>Runs the host CLI with the given arguments.</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || (args[0] is "--help" or "-h"))
            return 0;

        // Program passes the command name first; the options follow it.
        var options = args[0] == "rebuild-index" ? args[1..] : args;
        string? configPath = null;
        for (var i = 0; i < options.Length; i += 2)
        {
            // The only option is --config <path>; anything else, such as --config=<path>, is refused rather than ignored.
            if (options[i] is not ("--config" or "-c"))
            {
                Console.Error.WriteLine($"Rebuild failed: unknown argument '{options[i]}'. Use --config <path>.");
                return 1;
            }
            if (i + 1 >= options.Length)
            {
                Console.Error.WriteLine("Rebuild failed: --config needs a path.");
                return 1;
            }
            // A relative path means the working directory; when --config is given twice the first one is used.
            var candidate = Path.GetFullPath(options[i + 1]);
            if (!File.Exists(candidate))
            {
                Console.Error.WriteLine($"Rebuild failed: config file '{candidate}' was not found.");
                return 1;
            }
            configPath ??= candidate;
        }

        await HandleRebuildIndex(configPath, ct);
        return 0;
    }

    private static async Task HandleRebuildIndex(string? configPath, CancellationToken ct)
    {
        // AddJsonFile resolves a relative path next to the program; the check below and the user mean the working directory.
        configPath = Path.GetFullPath(configPath ?? "appsettings.json");

        try
        {
            var cfg = new ConfigurationBuilder()
                .AddJsonFile(configPath, optional: !File.Exists(configPath))
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(cfg);
            services.AddOptions<AmpelumHostOptions>()
                .BindConfiguration(AmpelumHostOptions.SectionName);
            services.AddSingleton<IHostSqliteConnectionFactory, HostSqliteConnectionFactory>();
            services.AddSingleton<IAuditIndex, AuditIndex>();
            services.AddSingleton<IPromptVersionStore, PromptVersionStore>();
            services.AddSingleton<IOptimizerRunStore, OptimizerRunStore>();
            services.AddSingleton<IModuleRegistrySnapshotStore, ModuleRegistrySnapshotStore>();
            services.AddSingleton<IModuleRegistry, ModuleRegistry.ModuleRegistry>();
            services.AddSingleton<IIndexRebuilder, IndexRebuilder>();

            var provider = services.BuildServiceProvider();
            var rebuilder = provider.GetRequiredService<IIndexRebuilder>();

            var summary = await rebuilder.RebuildAsync(ct);
            Console.WriteLine($"Rebuild complete: {summary.AuditEntriesIndexed} audit entries, " +
                $"{summary.PromptVersionsIndexed} prompt versions, {summary.ModulesSnapshot} modules " +
                $"in {summary.Duration.TotalMilliseconds:F0}ms");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Rebuild failed: {ex.Message}");
            throw;
        }
    }
}
