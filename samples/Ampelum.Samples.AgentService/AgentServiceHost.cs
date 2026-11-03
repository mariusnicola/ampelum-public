using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Host;

namespace Ampelum.Samples.AgentService;

/// <summary>Wires the Ampelum Host around one Predict module. The caller picks the server (Kestrel or TestServer).</summary>
public static class AgentServiceHost
{
    public const string ModuleName = "Answer";

    /// <param name="builder">Web application builder; the caller has already chosen the server.</param>
    /// <param name="chat">Model behind the module.</param>
    /// <param name="hostApiKey">Host:OpenRouter:ApiKey. The Host refuses to map a module without it.</param>
    /// <param name="storageDir">Directory for the Host's SQLite file, audit file and prompt store.</param>
    public static WebApplication Build(WebApplicationBuilder builder, IChatClient chat, string hostApiKey, string storageDir)
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Host:Storage:SqlitePath"] = Path.Combine(storageDir, "host.db"),
            ["Host:Storage:AuditFile"] = Path.Combine(storageDir, "audit.jsonl"),
            ["Host:Storage:PromptDir"] = Path.Combine(storageDir, "prompts"),
            ["Host:OpenRouter:ApiKey"] = hostApiKey,
        });

        builder.Services.AddSingleton(chat);
        builder.Services.AddAmpelum().AddAuditHook();
        builder.Services.AddSingleton<IHook>(_ => new BudgetHook(null));
        builder.Services.AddAmpelumHost();
        builder.Services.AddOpenApi();

        var app = builder.Build();
        app.MapAmpelumModule<AnswerSignature>(
            ModuleName,
            sp => sp.GetRequiredService<Predict<AnswerSignature>>(),
            supportsStreaming: true);
        app.MapAmpelumHostEndpoints();
        return app;
    }
}
