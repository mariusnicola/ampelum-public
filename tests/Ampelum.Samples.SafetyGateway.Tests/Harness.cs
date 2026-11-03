using System.Text.Json;
using Ampelum.Samples.SafetyGateway;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Tools;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Samples.SafetyGateway.Tests;

/// <summary>A folder that exists for one test and is removed afterwards.</summary>
sealed class TempFolder : IDisposable
{
    readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ampelum-safety-test-");

    public string Path => _directory.FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => _directory.Delete(recursive: true);
}

/// <summary>Counts what the tools ask the sandbox to run, then lets the real sandbox decide.</summary>
sealed class SpySandbox : IExecutionSandbox
{
    readonly ProcessSandbox _inner = new();

    public List<SandboxCommand> Commands { get; } = [];

    public SandboxResult? LastResult { get; private set; }

    public async ValueTask<SandboxResult> ExecuteAsync(SandboxCommand command, CancellationToken cancellationToken = default)
    {
        Commands.Add(command);
        return LastResult = await _inner.ExecuteAsync(command, cancellationToken);
    }
}

/// <summary>A real signing provider whose tokens can be switched off mid-run, to stand in for an identity revoked while the agent is running.</summary>
sealed class RevocableIdentityProvider : IAgentIdentityProvider
{
    readonly LocalJwtIdentityProvider _inner = new();

    public bool Revoked { get; set; }

    public Task<AgentJwt> IssueAsync(string agentType, string[] scope, TimeSpan runDuration, CancellationToken ct = default) =>
        _inner.IssueAsync(agentType, scope, runDuration, ct);

    public Task<AgentIdentity?> VerifyAsync(string token, CancellationToken ct = default) =>
        Revoked ? Task.FromResult<AgentIdentity?>(null) : _inner.VerifyAsync(token, ct);

    public Task<AgentJwt> RenewAsync(AgentJwt current, TimeSpan extension, CancellationToken ct = default) =>
        _inner.RenewAsync(current, extension, ct);

    public Task<AgentJwt> ResumeAsync(
        string agentType, string[] scope, string priorInstanceId, TimeSpan runDuration, CancellationToken ct = default) =>
        _inner.ResumeAsync(agentType, scope, priorInstanceId, runDuration, ct);
}

/// <summary>One gateway wired to fakes, a per-test folder and rates supplied here.</summary>
sealed class Harness : IAsyncDisposable
{
    public static readonly ModelRateTable Rates = new(InputPerToken: 0.000001m, OutputPerToken: 0.000003m);

    readonly bool _ownsFolder;

    Harness(TempFolder folder, bool ownsFolder)
    {
        Folder = folder;
        _ownsFolder = ownsFolder;
        Trust = new FileTrustStore(folder.File("trust"));
    }

    public TempFolder Folder { get; }
    public FakeChatClient Chat { get; } = new();
    public FakeModerationHandler Moderation { get; } = new(FakeData.UnsafeMarker);
    public CapturingTraceSink Trace { get; } = new();
    public InMemorySecretStore Secrets { get; } = new();
    public HandbookStore Handbook { get; } = new(FakeData.HandbookPages());
    public SpySandbox Sandbox { get; } = new();
    public IEmployeeDirectory EmployeeDirectory { get; private set; } = new InMemoryDirectory(FakeData.DirectoryEntries, FakeData.DirectoryToken);
    public FileTrustStore Trust { get; }
    public RevocableIdentityProvider Identity { get; } = new();
    public GatewayOptions Options { get; private set; } = null!;
    public AgentJwt Jwt { get; private set; } = null!;
    public SafetyGateway Gateway { get; private set; } = null!;

    public string AuditPath => Options.AuditPath;

    public static async Task<Harness> StartAsync(
        Func<GatewayOptions, GatewayOptions>? configure = null,
        TempFolder? folder = null,
        bool replay = false,
        TimeSpan? lifetime = null,
        bool start = true,
        IEmployeeDirectory? directory = null)
    {
        var harness = new Harness(folder ?? new TempFolder(), ownsFolder: folder is null);
        if (directory is not null)
            harness.EmployeeDirectory = directory;
        var options = GatewayOptions.Helpdesk(harness.Folder.File("audit.jsonl"), Rates);
        harness.Options = configure?.Invoke(options) ?? options;
        harness.Jwt = await harness.Identity.IssueAsync(
            harness.Options.AgentType, [.. harness.Options.Scope], lifetime ?? harness.Options.RunDuration);
        await harness.ProvisionSecretsAsync(harness.Jwt.Claims.InstanceId);
        if (start)
            harness.Gateway = await harness.StartGatewayAsync(replay);
        return harness;
    }

    public async Task ProvisionSecretsAsync(string instanceId)
    {
        await Secrets.StoreAsync(Options.ModerationKeyAlias, FakeData.ModerationKey, instanceId);
        await Secrets.StoreAsync(Options.DirectoryCredentialAlias, FakeData.DirectoryToken, instanceId);
    }

    public Task<SafetyGateway> StartGatewayAsync(bool replay = false) =>
        SafetyGateway.StartAsync(Dependencies(replay), Jwt, TestContext.Current.CancellationToken);

    public GatewayDependencies Dependencies(bool replay = false) => new(
        Chat, Options, Secrets, Identity, Trust, Moderation,
        EmployeeDirectory, Handbook, Sandbox)
    {
        Trace = Trace,
        ReplayAudit = replay,
    };

    /// <summary>Everything the model was sent in one call, as a single string.</summary>
    public string Sent(int call) => string.Join("\n", Chat.RecordedCalls[call].Messages.Select(MessageText));

    /// <summary>Text of a message including tool results, which carry no plain text of their own.</summary>
    static string MessageText(ChatMessage message) => string.Join("\n", message.Contents.Select(content => content switch
    {
        TextContent text => text.Text,
        FunctionResultContent result => result.Result?.ToString() ?? "",
        FunctionCallContent call => $"{call.Name}({JsonSerializer.Serialize(call.Arguments)})",
        _ => "",
    }));

    /// <summary>The audit file as written so far; the gateway keeps it open, so it is read with sharing.</summary>
    public string AuditText()
    {
        using var stream = new FileStream(AuditPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Input and output values of every audit entry, unescaped, one per line.</summary>
    public async Task<string> AuditValuesAsync() => string.Join(
        '\n', (await AuditEntriesAsync()).SelectMany(e => e.Inputs.Values.Concat(e.Outputs.Values)));

    public async Task<IReadOnlyList<AuditEntry>> AuditEntriesAsync()
    {
        var sink = await AuditFile.LoadAsync(AuditPath, TestContext.Current.CancellationToken);
        var entries = new List<AuditEntry>();
        await foreach (var entry in sink.EnumerateAsync(TestContext.Current.CancellationToken))
            entries.Add(entry);
        return entries;
    }

    public async ValueTask DisposeAsync()
    {
        if (Gateway is not null)
            await Gateway.DisposeAsync();
        if (_ownsFolder)
            Folder.Dispose();
    }
}
