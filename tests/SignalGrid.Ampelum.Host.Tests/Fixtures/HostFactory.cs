#pragma warning disable AMPELUM_V09_PREVIEW
using SignalGrid.Ampelum.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Fixtures;

/// <summary>
/// Spins up the real <see cref="Program"/> entry-point via
/// <see cref="WebApplicationFactory{TEntryPoint}"/> for integration tests.
/// </summary>
internal sealed class HostFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public TempStorageFixture Storage { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Host:Storage:SqlitePath"]   = Storage.SqlitePath,
            ["Host:Storage:AuditFile"]    = Storage.AuditFile,
            ["Host:Storage:PromptDir"]    = Storage.PromptDir,
            ["Host:OpenRouter:ApiKey"]    = "test-key",
            ["Host:Sse:KeepAliveSeconds"] = "1",
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<Microsoft.Extensions.AI.IChatClient>();
            services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(new FakeChatClient());
        });
    }

    public override async ValueTask DisposeAsync() { Storage.Dispose(); await base.DisposeAsync(); }
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
}
