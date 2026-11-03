using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Host;
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

    TaskCompletionSource _gate = OpenGateSource();
    readonly TaskCompletionSource _modelReached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    static TaskCompletionSource OpenGateSource()
    {
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        open.SetResult();
        return open;
    }

    /// <summary>Holds every model call until <see cref="OpenGate"/>, so a test can look at a run that is still in flight.</summary>
    public void CloseGate() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void OpenGate() => _gate.TrySetResult();

    /// <summary>Completes when a model call has arrived at the gate.</summary>
    public Task ModelReached => _modelReached.Task;

    readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once a client of the optimizer progress stream is registered with the Host's progress bus.</summary>
    public Task ProgressSubscribed => _subscribed.Task;

    /// <summary>Wraps the Host's progress bus so the fixture learns when a subscriber is registered.</summary>
    void ConfigureServices(IServiceCollection services)
    {
        ObserveProgressBus(services);
    }

    void ObserveProgressBus(IServiceCollection services)
    {
        var bus = services.Single(s => s.ServiceType == typeof(IOptimizerProgressBus));
        services.Remove(bus);
        services.AddSingleton<IOptimizerProgressBus>(sp => new ObservedBus(
            (IOptimizerProgressBus)ActivatorUtilities.CreateInstance(sp, bus.ImplementationType!),
            () => _subscribed.TrySetResult()));
    }

    sealed class ObservedBus(IOptimizerProgressBus inner, Action subscribed) : IOptimizerProgressBus
    {
        public void Publish(string runId, SseEnvelope envelope) => inner.Publish(runId, envelope);

        public void Complete(string runId) => inner.Complete(runId);

        public async IAsyncEnumerable<SseEnvelope> Subscribe(
            string runId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await using var events = inner.Subscribe(runId, ct).GetAsyncEnumerator(ct);
            // The bus registers a subscriber while the first MoveNextAsync call runs its synchronous part,
            // so once that call has been made the subscriber is in place.
            var next = events.MoveNextAsync();
            subscribed();
            while (await next)
            {
                yield return events.Current;
                next = events.MoveNextAsync();
            }
        }
    }

    sealed class GatedChatClient(IChatClient inner, ServiceFixture owner) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            owner._modelReached.TrySetResult();
            await owner._gate.Task.WaitAsync(cancellationToken);
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
    }

    /// <summary>Directory holding the Host's SQLite file, audit file and prompt folder for this instance.</summary>
    public string StorageDir => _storageDir;

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
        _app = AgentServiceHost.Build(
            builder, new GatedChatClient(Model, this), "placeholder-host-key", _storageDir, configureServices: ConfigureServices);
        await _app.StartAsync();
        _http = _app.GetTestClient();
        Client = new AgentServiceClient(_http);
    }

    public async ValueTask DisposeAsync()
    {
        OpenGate();
        _http?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        // The Host's SQLite connections are pooled and keep the database file open until the pools are cleared.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

    public ServiceFixture EnqueueStreamingAnswer(string answer)
    {
        Model.EnqueueStreamingOutputs(new AnswerSignature { Question = "", Answer = answer });
        return this;
    }

    /// <summary>All text the model was sent in one call.</summary>
    public string SentText(int call) =>
        string.Join("\n", Model.RecordedCalls[call].Messages.Select(m => m.Text));
}
