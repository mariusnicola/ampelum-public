using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Samples.AgentService.Tests;

/// <summary>
/// The sample's Host on an in-memory TestServer, so a test makes real HTTP requests without a port.
/// Storage goes to a directory owned by this instance.
/// </summary>
public sealed class ServiceFixture : IAsyncLifetime
{
    readonly string _storageDir = Path.Combine(Path.GetTempPath(), "ampelum-agent-service-tests-" + Guid.NewGuid().ToString("N"));
    WebApplication? _app;
    HttpClient? _http;

    public FakeChatClient Model { get; } = new();

    public AgentServiceClient Client { get; private set; } = null!;

    /// <summary>The raw client, for requests the typed client would not send.</summary>
    public HttpClient Http => _http ?? throw new InvalidOperationException("Not initialized.");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_storageDir);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // The Host refuses to map a module without a key even when the model is a fake and never uses it.
        _app = AgentServiceHost.Build(builder, Model, "placeholder-host-key", _storageDir);
        await _app.StartAsync();
        _http = _app.GetTestClient();
        Client = new AgentServiceClient(_http);
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        try
        {
            Directory.Delete(_storageDir, recursive: true);
        }
        catch (IOException)
        {
            // A SQLite handle can outlive the host briefly; the directory is in the temp folder.
        }
    }

    public ServiceFixture EnqueueAnswer(string answer)
    {
        Model.EnqueueOutputs(new AnswerSignature { Question = "", Answer = answer });
        return this;
    }

    /// <summary>All text the model was sent in one call.</summary>
    public string SentText(int call) =>
        string.Join("\n", Model.RecordedCalls[call].Messages.Select(m => m.Text));
}
