using System.Globalization;
using Ampelum.Samples.SafetyGateway;
using Ampelum.Samples.Shared;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Tools;
using SignalGrid.Ampelum.Safety.Moderation;

var fake = args.Contains("--fake");
var workDir = Directory.CreateTempSubdirectory("ampelum-safety-gateway-");
try
{
    return await RunAsync();
}
finally
{
    workDir.Delete(recursive: true);
}

async Task<int> RunAsync()
{
    IChatClient chat;
    HttpMessageHandler moderation;
    ModelRateTable rates;
    Uri? moderationEndpoint = null;
    string moderationKey;
    string directoryToken;

    if (fake)
    {
        chat = FakeScript.Demo();
        moderation = new FakeModerationHandler(FakeData.UnsafeMarker);
        rates = FakeData.Rates;
        moderationKey = FakeData.ModerationKey;
        directoryToken = FakeData.DirectoryToken;
    }
    else
    {
        var config = SampleConfig.Load();
        chat = config.CreateChatClient();
        moderation = new SocketsHttpHandler();
        moderationEndpoint = new Uri(config.Require("MODERATION_ENDPOINT"));
        moderationKey = config.Require("MODERATION_API_KEY");
        rates = new ModelRateTable(
            RateSetting.Parse(config.Require("MODEL_INPUT_RATE")),
            RateSetting.Parse(config.Require("MODEL_OUTPUT_RATE")));
        // The directory here is a stand-in, so it accepts a credential made up for this run.
        directoryToken = Guid.NewGuid().ToString("N");
    }

    var options = GatewayOptions.Helpdesk(Path.Combine(workDir.FullName, "audit.jsonl"), rates, moderationEndpoint);
    var identityProvider = new LocalJwtIdentityProvider();
    var jwt = await identityProvider.IssueAsync(options.AgentType, [.. options.Scope], options.RunDuration);

    // Secrets live under the run's instance id. An AMPL_SECRET_<alias> environment variable is the fallback.
    var memory = new InMemorySecretStore();
    await memory.StoreAsync(options.ModerationKeyAlias, moderationKey, jwt.Claims.InstanceId);
    await memory.StoreAsync(options.DirectoryCredentialAlias, directoryToken, jwt.Claims.InstanceId);
    var secrets = new ChainSecretStore([memory, new EnvironmentSecretStore()]);

    var deps = new GatewayDependencies(
        chat, options, secrets, identityProvider, new FileTrustStore(Path.Combine(workDir.FullName, "trust")),
        moderation, new InMemoryDirectory(FakeData.DirectoryEntries, directoryToken),
        new HandbookStore(FakeData.HandbookPages()), new ProcessSandbox());

    await using var gateway = await SafetyGateway.StartAsync(deps, jwt);
    Console.WriteLine($"Agent:   {gateway.Identity.AgentType} instance {gateway.Identity.InstanceId[..8]}, scope {string.Join(" ", gateway.Identity.Scope)}");

    var answer = await TryAsync(() => gateway.AskAsync(DemoQuestions.Leave));
    if (answer is not null)
        Console.WriteLine($"Ask:     {answer.Text}");

    var withTools = await TryAsync(() => gateway.AskWithToolsAsync(DemoQuestions.Tools));
    if (withTools is not null)
    {
        Console.WriteLine($"Tools:   {withTools.Text}");
        Console.WriteLine($"Ran:     {string.Join(", ", withTools.ToolsRun)}");
        foreach (var refusal in withTools.Refusals)
            Console.WriteLine($"Refused: {refusal.Tool} by {refusal.Layer}: {refusal.Reason}");
    }

    if (fake)
        await TryAsync(() => gateway.AskAsync(DemoQuestions.Unsafe));

    var usage = gateway.Usage();
    Console.WriteLine($"Usage:   {usage.Tokens.RequestCount} calls, {usage.Tokens.PromptTokens} in, {usage.Tokens.CompletionTokens} out, estimated cost {usage.Cost.EstimatedCostUsd?.ToString("0.000000", CultureInfo.InvariantCulture)} USD");
    Console.WriteLine($"Trust:   {await gateway.TrustAsync()}");

    var chain = await gateway.VerifyAuditAsync();
    Console.WriteLine($"Audit:   {gateway.AuditEntries} entries, chain valid = {chain.IsValid}");
    return chain.IsValid ? 0 : 1;

    // A flagged reply and an unreachable moderation service both withhold the reply; the run goes on to its summary.
    static async Task<GatewayReply?> TryAsync(Func<Task<GatewayReply>> ask)
    {
        try
        {
            return await ask();
        }
        catch (ModerationException ex)
        {
            Console.WriteLine($"Blocked: reply held back, moderation categories {string.Join(",", ex.Categories)}");
        }
        catch (ModerationUnavailableException ex)
        {
            Console.WriteLine($"Blocked: reply held back, moderation service unavailable ({ex.InnerException?.GetType().Name})");
        }
        return null;
    }
}
