using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Tools;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.ProviderAdapters;
using SignalGrid.Ampelum.Safety.Moderation;
using SignalGrid.Ampelum.Safety.Pii;
using SignalGrid.Ampelum.Safety.Tools;
using SignalGrid.Ampelum.Usage;

namespace Ampelum.Samples.SafetyGateway;

/// <summary>
/// Every model call of an internal assistant passes one safety layer: identity check, known-key and injection
/// scan, PII redaction in the audit trail, moderation of the output, a gated tool set and an audit chain.
/// Safety exceptions from the hooks propagate so the caller decides what a blocked reply means.
/// </summary>
public sealed class SafetyGateway : IAsyncDisposable
{
    readonly GatewayDependencies _deps;
    readonly AgentJwt _jwt;
    readonly ServiceProvider _provider;
    readonly HttpClient _moderationClient;
    readonly IUsageTracker _usage;
    readonly SafetyNotes _notes;
    readonly OutboundSanitizer _sanitizer = OutboundSanitizer.Default;

    SafetyGateway(
        GatewayDependencies deps, AgentJwt jwt, ServiceProvider provider, HttpClient moderationClient, IUsageTracker usage,
        AuditedModerator moderator)
    {
        _deps = deps;
        _jwt = jwt;
        _provider = provider;
        _moderationClient = moderationClient;
        _usage = usage;
        _notes = new SafetyNotes(provider.GetServices<IHook>().OfType<AuditHook>().Single(), jwt.Claims.InstanceId);
        moderator.Notes = _notes;
    }

    public AgentIdentity Identity => _jwt.Claims;

    /// <summary>
    /// Checks the identity, reads the moderation key from the secret store under the run's instance id, and wires the hooks.
    /// A missing secret fails here, before any question is accepted.
    /// </summary>
    public static async Task<SafetyGateway> StartAsync(GatewayDependencies deps, AgentJwt jwt, CancellationToken ct = default)
    {
        var identity = await deps.Identity.VerifyAsync(jwt.Token, ct)
            ?? throw new UnauthorizedAccessException("The agent identity token is invalid or expired.");
        var options = deps.Options;
        // The audit hook starts every chain from the zero hash, so appending to a used file would break the chain.
        if (!deps.ReplayAudit && File.Exists(options.AuditPath) && new FileInfo(options.AuditPath).Length > 0)
            throw new InvalidOperationException($"The audit file {options.AuditPath} already has entries; use a new file per run.");
        var moderationKey = await deps.Secrets.ResolveAsync(options.ModerationKeyAlias, identity.InstanceId, ct);
        var client = new HttpClient(deps.ModerationHandler, disposeHandler: false);
        var moderator = new AuditedModerator(new OpenAiModerator(client, moderationKey, options.ModerationEndpoint));

        var usage = new DefaultUsageTracker();
        var services = new ServiceCollection();
        services.AddSingleton(deps.Chat);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IUsageTracker>(usage);
        if (deps.Trace is not null)
            services.AddSingleton(deps.Trace);

        var builder = services.AddAmpelum().UseAdapter<JsonAdapter>();
        if (options.BatchAudit && !deps.ReplayAudit)
        {
            // The batched sink does not dispose the sink it wraps, so the container owns the file sink and closes it last.
            services.AddSingleton(_ => new JsonFileAuditSink(options.AuditPath));
            services.AddSingleton<IAuditSink>(sp => new BatchedAuditSink(sp.GetRequiredService<JsonFileAuditSink>()));
        }
        else
            builder.AddJsonFileAuditSink(options.AuditPath);
        builder.AddAuditHook(deps.ReplayAudit);

        services.AddAmpelumToolRegistry(registry =>
        {
            foreach (var tool in ToolCatalog.All)
                registry.Register(tool.Registration);
        });
        services.AddSingleton<IHook>(new IdentityStampHook(identity));
        services.AddSingleton<IHook>(new PiiRedactionHook(new RegexPiiRedactor()));
        services.AddSingleton<IHook>(new CredentialRedactionHook(OutboundSanitizer.Default));
        // Fail closed: if the moderation service cannot be reached, the reply is not released.
        services.AddSingleton<IHook>(new ModerationHook(moderator, failOpen: false));

        return new SafetyGateway(deps, new AgentJwt(jwt.Token, identity), services.BuildServiceProvider(), client, usage, moderator);
    }

    /// <summary>One model call, no tools. The only operation that can be replayed from the audit file.</summary>
    public async Task<GatewayReply> AskAsync(string question, CancellationToken ct = default)
    {
        var (clean, flags) = await ScreenQuestionAsync(question, ct);
        using var scope = _provider.CreateScope();
        using var usageScope = _usage.Scope("ask");
        var predict = scope.ServiceProvider.GetRequiredService<Predict<PolicyAnswerSignature>>();
        try
        {
            var answer = (await predict.ForwardAsync(new PolicyAnswerSignature { Question = clean }, ct: ct)).Value.Answer;
            return new GatewayReply(await ScreenReplyAsync(answer, ct), flags, [], []);
        }
        catch (ModerationException ex)
        {
            await RecordBlockedAsync(ex, ct);
            throw;
        }
    }

    /// <summary>A ReAct loop whose tools pass the gate, the mode and trust policy and the outbound sanitizer.</summary>
    public async Task<GatewayReply> AskWithToolsAsync(string question, CancellationToken ct = default)
    {
        var (clean, flags) = await ScreenQuestionAsync(question, ct);
        using var governance = await ToolGovernance.CreateAsync(
            _provider.GetRequiredService<IToolRegistry>(), _deps.Trust, Identity, _deps.Options, ct);
        var toolLog = new InMemoryToolAuditLog();
        var dispatcher = governance.CreateDispatcher(toolLog);
        var handlers = new ToolHandlers(
            new SecretRefResolver(_deps.Secrets), Identity.InstanceId, _deps.Directory, _deps.Handbook, _deps.Sandbox,
            AllowedAliases());
        foreach (var (name, handler) in handlers.ByQualifiedName)
            dispatcher.RegisterHandler(name, handler);

        var refusals = new List<ToolRefusal>();
        var tools = ToolCatalog.All
            .Select(d => (AIFunction)new GovernedTool(d, governance, dispatcher, toolLog, _sanitizer, _notes, refusals))
            .ToArray();

        using var scope = _provider.CreateScope();
        using var usageScope = _usage.Scope("ask-with-tools");
        var react = new ReAct<ToolAnswerSignature>(
            scope.ServiceProvider.GetRequiredService<AmpelumContext>(), tools, _deps.Options.MaxToolIterations);
        try
        {
            var prediction = await react.ForwardAsync(new ToolAnswerSignature { Question = clean }, ct: ct);
            var calls = (IReadOnlyList<ToolCallRecord>)prediction.Trace.Metadata[TraceKeys.ToolCalls]!;
            var toolsRun = calls.Where(c => c.Error is null).Select(c => c.FunctionName).ToArray();
            await RecordOutcomeAsync(refusals, calls.Count, succeeded: true, ct);
            return new GatewayReply(await ScreenReplyAsync(prediction.Value.Answer, ct), flags, refusals, toolsRun);
        }
        catch (ModerationException ex)
        {
            await RecordBlockedAsync(ex, ct);
            await RecordOutcomeAsync(refusals, refusals.Count, succeeded: false, ct);
            throw;
        }
    }

    /// <summary>
    /// Checks the tool calls a provider response asked for before anyone executes them. Nothing runs here;
    /// the verdicts say which calls the gate and the policy would let through.
    /// </summary>
    public async Task<IReadOnlyList<ToolCallVerdict>> ReviewToolCallsAsync(
        ProviderFormat format, string providerJson, CancellationToken ct = default)
    {
        var calls = format switch
        {
            ProviderFormat.OpenAi => ToolCallFormatConverter.FromOpenAi(providerJson),
            ProviderFormat.Anthropic => ToolCallFormatConverter.FromAnthropic(providerJson),
            ProviderFormat.Gemini => ToolCallFormatConverter.FromGemini(providerJson),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        using var governance = await ToolGovernance.CreateAsync(
            _provider.GetRequiredService<IToolRegistry>(), _deps.Trust, Identity, _deps.Options, ct);
        var verdicts = new List<ToolCallVerdict>();
        foreach (var call in calls)
        {
            var refusal = await governance.PreflightAsync(call.Name, call.Arguments, AllowedAliases(), ct);
            if (refusal is not null)
                await _notes.RecordRefusalAsync(refusal, ct);
            verdicts.Add(new ToolCallVerdict(call, refusal));
        }
        return verdicts;
    }

    IReadOnlyDictionary<string, string[]> AllowedAliases() =>
        new Dictionary<string, string[]> { [ToolCatalog.Search] = [_deps.Options.DirectoryCredentialAlias] };

    /// <summary>Token counts per operation, and their cost at the rates in the options.</summary>
    public GatewayUsage Usage()
    {
        var tokens = _usage.Snapshot();
        var rates = _deps.Options.Rates;
        return new GatewayUsage(
            tokens,
            new ChatCostSummary(tokens.PromptTokens, tokens.CompletionTokens, rates.Estimate(tokens.PromptTokens, tokens.CompletionTokens)));
    }

    public Task<TrustLevel> TrustAsync(CancellationToken ct = default) =>
        _deps.Trust.GetTrustLevelAsync(Identity.AgentType, _deps.Options.Domain, ct);

    /// <summary>The sink the audit hook writes to: the file sink, or the batched sink when batching is on.</summary>
    public IAuditSink AuditSink => _provider.GetRequiredService<IAuditSink>();

    public long AuditEntries => AuditSink.NextSequence;

    /// <summary>
    /// Checks the whole audit file; an edited, removed or reordered entry makes the chain invalid.
    /// With batched audit the file is complete only after the gateway is disposed.
    /// </summary>
    public async Task<AuditChainVerificationResult> VerifyAuditAsync(CancellationToken ct = default)
    {
        await using var file = new JsonFileAuditSink(_deps.Options.AuditPath);
        return await AuditChainVerifier.VerifyAsync(file, ct);
    }

    async Task<(string Clean, IReadOnlyList<string> Flags)> ScreenQuestionAsync(string question, CancellationToken ct)
    {
        if (await _deps.Identity.VerifyAsync(_jwt.Token, ct) is null)
            throw new UnauthorizedAccessException("The agent identity token is invalid or expired.");

        // UserInput is flagged but never rewritten, so only the injection pass is read from it.
        var flags = _sanitizer.Sanitize(question, ScanSource.UserInput).Detections
            .Where(d => d.Type == DetectionType.InjectionAttempt).Select(d => d.Rule).Distinct().ToArray();
        if (flags.Length > 0)
        {
            await _notes.RecordAsync(SafetyNotes.InputFlagged, new Dictionary<string, object?>
            {
                ["rules"] = string.Join(",", flags),
            }, ct);
        }

        // LogOutput is the source that replaces credentials, but only the known key shapes in OutboundSanitizer.
        // Other secrets (plain passwords, bearer headers, connection URLs, bare hex) pass through; see the README.
        return (_sanitizer.Sanitize(question, ScanSource.LogOutput).SanitizedContent, flags);
    }

    async Task<string> ScreenReplyAsync(string reply, CancellationToken ct)
    {
        var scan = _sanitizer.Sanitize(reply, ScanSource.LogOutput);
        if (scan.Detections.Count > 0)
        {
            await _notes.RecordAsync(SafetyNotes.ReplySanitized, new Dictionary<string, object?>
            {
                ["rules"] = string.Join(",", scan.Detections.Select(d => d.Rule)),
            }, ct);
        }
        return scan.SanitizedContent;
    }

    // The after-hooks stop at the moderation hook, so a blocked reply never reaches the audit hook; this entry records it.
    async Task RecordBlockedAsync(ModerationException ex, CancellationToken ct) =>
        await _notes.RecordAsync(SafetyNotes.ReplyBlocked, new Dictionary<string, object?>
        {
            ["categories"] = string.Join(",", ex.Categories),
        }, ct);

    // A refused tool request counts as a security violation and lowers the agent's trust.
    async Task RecordOutcomeAsync(IReadOnlyList<ToolRefusal> refusals, int toolCalls, bool succeeded, CancellationToken ct)
    {
        var violation = refusals.Any(r => r.Layer is ToolGovernance.GateLayer or ToolGovernance.PolicyLayer);
        await _deps.Trust.RecordOutcomeAsync(
            Identity.AgentType,
            _deps.Options.Domain,
            new TrustOutcome(Identity.InstanceId, succeeded, violation, HadEscalation: false, toolCalls, DateTimeOffset.UtcNow),
            ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        _moderationClient.Dispose();
    }
}
