#pragma warning disable AMPELUM_V09_PREVIEW
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace SignalGrid.Ampelum.Host.Tests.Fixtures;

// Marker class used as the WebApplicationFactory<T> entry point.
// ConfigureServices / Configure are discovered via ASP.NET Core startup conventions.
public sealed class TestStartup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClient>(new FakeChatClient());
        services.AddAmpelum()
            .AddAmpelumHook<AuditHook>()
            .AddAmpelumHook<BudgetHook>();
        services.AddAmpelumHost();
        services.AddOpenApi();
        services.AddLogging();
    }

    public void Configure(IApplicationBuilder app)
    {
        var webApp = (WebApplication)app;
        webApp.UseMiddleware<SignalGrid.Ampelum.Host.ErrorHandling.ProblemDetailsMiddleware>();
        // Temporary: explicit healthz until task 3.18 adds MapHealthEndpoints() inside MapAmpelumHostEndpoints.
        webApp.MapGet("/healthz", () => Results.Ok());
        webApp.MapAmpelumModule<FakeSig>("Fake",
            sp => sp.GetRequiredService<Predict<FakeSig>>(),
            supportsStreaming: true);
        webApp.MapAmpelumHostEndpoints();
    }
}
