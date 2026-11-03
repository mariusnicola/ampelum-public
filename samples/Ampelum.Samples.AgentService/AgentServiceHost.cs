using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Host;

namespace Ampelum.Samples.AgentService;

/// <summary>Wires the Ampelum Host around one Predict module. The caller picks the server (Kestrel or TestServer).</summary>
public static class AgentServiceHost
{
    public const string ModuleName = "Answer";

    /// <summary>The module name an audit entry of a Predict call carries.</summary>
    public const string ReplayModuleName = "Predict";

    /// <param name="builder">Web application builder; the caller has already chosen the server.</param>
    /// <param name="chat">Model behind the module.</param>
    /// <param name="hostApiKey">Host:OpenRouter:ApiKey. The Host refuses to map a module without it.</param>
    /// <param name="storageDir">Directory for the Host's SQLite file, audit file and prompt store.</param>
    /// <param name="configureServices">Lets a caller adjust the services after the Host's are registered; tests use it to observe the Host.</param>
    /// <param name="replayModule">
    /// Also registers the program under <see cref="ReplayModuleName"/>. Replay looks the module up by the name the
    /// audit entry carries, which for a Predict call is always that name, so replay finds nothing without it.
    /// </param>
    public static WebApplication Build(
        WebApplicationBuilder builder, IChatClient chat, string hostApiKey, string storageDir, bool replayModule = false,
        Action<IServiceCollection>? configureServices = null)
    {
        var auditFile = Path.Combine(storageDir, "audit.jsonl");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Host:Storage:SqlitePath"] = Path.Combine(storageDir, "host.db"),
            ["Host:Storage:AuditFile"] = auditFile,
            ["Host:Storage:PromptDir"] = Path.Combine(storageDir, "prompts"),
            ["Host:OpenRouter:ApiKey"] = hostApiKey,
            // Without this the Host does not map /admin/rebuild-index.
            ["Host:AdminEndpointsEnabled"] = "true",
        });

        builder.Services.AddSingleton(chat);
        // The original sample used the in-memory sink the hook falls back to. It is swapped for a file-backed one
        // because the Host builds its audit index (audit by trace id, replay) only from the audit file.
        builder.Services.AddAmpelum().AddAuditHook();
        builder.Services.RemoveAll<IAuditSink>();
        builder.Services.AddSingleton<IAuditSink>(_ => new JsonFileAuditSink(auditFile));
        builder.Services.AddSingleton<IHook>(_ => new BudgetHook(null));
        builder.Services.AddAmpelumHost();
        builder.Services.AddOpenApi();
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapAmpelumModule<AnswerSignature>(
            ModuleName,
            sp => sp.GetRequiredService<Predict<AnswerSignature>>(),
            supportsStreaming: true);
        if (replayModule)
            app.MapAmpelumModule<AnswerSignature>(ReplayModuleName, sp => sp.GetRequiredService<Predict<AnswerSignature>>());
        app.MapAmpelumHostEndpoints();
        return app;
    }
}
