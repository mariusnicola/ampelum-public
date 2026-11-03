#pragma warning disable AMPELUM_V09_PREVIEW
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SignalGrid.Ampelum.Host.ErrorHandling;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Fixtures;

/// <summary>
/// Builds a full WebApplication in-process (via TestServer) with the "Fake" module registered.
/// Use this for endpoint tests that require an actual module (ForwardEndpoint, ModulesEndpoint, etc.)
/// HostFactory (WebApplicationFactory&lt;Program&gt;) starts with zero modules; this factory adds the "Fake" module.
/// </summary>
internal sealed class ModuleTestFactory : IAsyncLifetime
{
    readonly TempStorageFixture _storage = new();
    WebApplication? _app;
    HttpClient? _httpClient;

    readonly IChatClient? _chatClientOverride;
    readonly Action<IServiceCollection>? _configureServices;
    readonly bool _supportsStreaming;

    /// <param name="chatClientOverride">Replaces the FakeChatClient registration, for tests that need a client that fails.</param>
    /// <param name="configureServices">Extra registrations, for tests that add a hook.</param>
    /// <param name="supportsStreaming">Whether the Fake module is mapped as streaming.</param>
    public ModuleTestFactory(IChatClient? chatClientOverride = null, Action<IServiceCollection>? configureServices = null, bool supportsStreaming = true)
    {
        _chatClientOverride = chatClientOverride;
        _configureServices = configureServices;
        _supportsStreaming = supportsStreaming;
    }

    public FakeChatClient FakeChatClient { get; } = new();
    public HttpClient HttpClient => _httpClient ?? throw new InvalidOperationException("Not initialized.");
    public IServiceProvider Services => _app?.Services ?? throw new InvalidOperationException("Not initialized.");

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        ((IConfigurationBuilder)builder.Configuration).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Host:Storage:SqlitePath"]   = _storage.SqlitePath,
            ["Host:Storage:AuditFile"]    = _storage.AuditFile,
            ["Host:Storage:PromptDir"]    = _storage.PromptDir,
            ["Host:OpenRouter:ApiKey"]    = "test-key",
            ["Host:Sse:KeepAliveSeconds"] = "1",
        });

        builder.Services.AddSingleton<IChatClient>(_chatClientOverride ?? FakeChatClient);
        builder.Services.AddAmpelum()
            .AddAuditHook();
        builder.Services.AddSingleton<IHook>(_ => new BudgetHook(null));
        _configureServices?.Invoke(builder.Services);
        builder.Services.AddAmpelumHost();
        builder.Services.AddOpenApi();
        builder.Services.AddLogging();

        _app = builder.Build();
        _app.UseMiddleware<ProblemDetailsMiddleware>();
        _app.MapAmpelumModule<FakeSig>("Fake",
            sp => sp.GetRequiredService<Predict<FakeSig>>(),
            supportsStreaming: _supportsStreaming);
        _app.MapAmpelumHostEndpoints();

        await _app.StartAsync();
        _httpClient = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _httpClient?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        _storage.Dispose();
    }
}
