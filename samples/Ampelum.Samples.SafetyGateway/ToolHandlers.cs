using System.Text.Json;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Tools;

namespace Ampelum.Samples.SafetyGateway;

using Handler = Func<IReadOnlyDictionary<string, JsonElement>, CancellationToken, Task<ToolResult>>;

/// <summary>What each tool does once the governance layers let it through.</summary>
public sealed class ToolHandlers(
    SecretRefResolver secrets, string sessionId, IEmployeeDirectory directory, HandbookStore handbook, IExecutionSandbox sandbox,
    IReadOnlyDictionary<string, string[]> allowedAliases)
{
    public IReadOnlyDictionary<string, Handler> ByQualifiedName => new Dictionary<string, Handler>
    {
        [ToolCatalog.QualifiedName(ToolCatalog.Search)] = SearchAsync,
        [ToolCatalog.QualifiedName(ToolCatalog.ReadFile)] = ReadFileAsync,
        [ToolCatalog.QualifiedName(ToolCatalog.WriteFile)] = WriteFileAsync,
        [ToolCatalog.QualifiedName(ToolCatalog.RunShell)] = RunShellAsync,
    };

    async Task<ToolResult> SearchAsync(IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
    {
        var manifest = ToolCatalog.All.First(t => t.Manifest.Name == ToolCatalog.Search).Manifest;
        var refFields = SecretRefResolver.ExtractSecretRefFields(manifest.ArgsSchema);
        // The model writes the alias, so each tool may only ask for the aliases it was given.
        foreach (var field in refFields)
        {
            if (args.TryGetValue(field, out var alias) && alias.ValueKind == JsonValueKind.String && AliasRule.Refuses(allowedAliases, ToolCatalog.Search, alias.GetString()))
                return Failed(ToolCatalog.Search, AliasRule.Reason);
        }
        var resolved = await secrets.ResolveSecretsAsync(refFields, args, sessionId, ct);
        var secret = resolved[ToolCatalog.CredentialArg].GetString() ?? "";
        try
        {
            var found = directory.Search(secret, resolved["query"].GetString() ?? "");
            if (found is null)
                return Failed(ToolCatalog.Search, "The directory rejected the credential.");
            var lines = found.Select(e => $"{e.Name} ({e.Team}): {e.Note}");
            return Succeeded(ToolCatalog.Search, Scrub(string.Join("\n", lines), secret));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(ToolCatalog.Search, Scrub(ex.Message, secret));
        }
        finally
        {
            // The resolved copy only lives for this call.
            SecretRefResolver.ClearResolvedSecrets(refFields, resolved);
        }
    }

    Task<ToolResult> ReadFileAsync(IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
    {
        var name = args["name"].GetString() ?? "";
        var text = handbook.Read(name);
        return Task.FromResult(text is null
            ? Failed(ToolCatalog.ReadFile, $"No document named {name}.")
            : Succeeded(ToolCatalog.ReadFile, text));
    }

    Task<ToolResult> WriteFileAsync(IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
    {
        var name = args["name"].GetString() ?? "";
        handbook.Write(name, args["content"].GetString() ?? "");
        return Task.FromResult(Succeeded(ToolCatalog.WriteFile, $"Wrote {name}."));
    }

    async Task<ToolResult> RunShellAsync(IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
    {
        var tokens = SandboxCommandParser.Tokenize(args["command"].GetString() ?? "");
        if (tokens.Length == 0)
            return Failed(ToolCatalog.RunShell, "Empty command.");
        var result = await sandbox.ExecuteAsync(new SandboxCommand
        {
            Program = tokens[0],
            Arguments = tokens[1..],
            // Network and file-write limits are required, so a sandbox that cannot enforce them must refuse.
            Policy = new SandboxPolicy
            {
                Network = new SandboxNetworkPolicy { Access = SandboxNetworkAccess.DenyAll },
                AllowFileWrites = false,
            },
        }, ct);
        return result.Outcome == SandboxOutcome.Succeeded
            ? Succeeded(ToolCatalog.RunShell, result.StandardOutput)
            : Failed(ToolCatalog.RunShell, result.RejectionReason ?? $"Sandbox outcome {result.Outcome}.");
    }

    // Exact match on the value this call resolved. Shape-based cleaning cannot know an arbitrary token, so this covers
    // only the secret the tool itself resolved, and only in what the tool returns or throws.
    static string Scrub(string text, string secret) =>
        secret.Length == 0 ? text : text.Replace(secret, "[REDACTED:resolved-secret]", StringComparison.Ordinal);

    static ToolResult Succeeded(string tool, string output) =>
        new(tool, true, output, null, DateTimeOffset.UtcNow, TimeSpan.Zero);

    static ToolResult Failed(string tool, string error) =>
        new(tool, false, null, error, DateTimeOffset.UtcNow, TimeSpan.Zero);
}
