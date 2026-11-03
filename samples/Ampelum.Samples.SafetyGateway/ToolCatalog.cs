using System.Text.Json;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Safety.Tools;

namespace Ampelum.Samples.SafetyGateway;

/// <summary>One tool as both governance layers see it: the registry entry and the manifest.</summary>
public sealed record ToolDefinition(ToolRegistration Registration, ToolManifest Manifest);

/// <summary>The tools the assistant may ask for. Only some of them are allowed to run.</summary>
public static class ToolCatalog
{
    public const string SourcePrefix = "helpdesk";
    public const string Version = "1";
    public const string Search = "search";
    public const string ReadFile = "read_file";
    public const string WriteFile = "write_file";
    public const string RunShell = "run_shell";

    /// <summary>Argument that carries a secret store alias; the tool receives the secret, the model never does.</summary>
    public const string CredentialArg = "credential";

    public static IReadOnlyList<ToolDefinition> All { get; } =
    [
        Define(Search, "Search the employee directory.", ToolCategory.Information, DangerTier.Safe,
            Reversibility.Reversible,
            """
            {"type":"object","properties":{
              "query":{"type":"string","description":"Name or team to look for."},
              "credential":{"type":"string","format":"secret-ref","description":"Alias of the directory credential. Pass the alias, never a value."}},
             "required":["query","credential"]}
            """),
        Define(ReadFile, "Read a handbook document by name.", ToolCategory.DataAccess, DangerTier.Safe,
            Reversibility.Reversible,
            """{"type":"object","properties":{"name":{"type":"string","description":"Document name."}},"required":["name"]}"""),
        Define(WriteFile, "Write a handbook document.", ToolCategory.DataMutation, DangerTier.Mutating,
            Reversibility.DurableWithUndo,
            """{"type":"object","properties":{"name":{"type":"string"},"content":{"type":"string"}},"required":["name","content"]}"""),
        Define(RunShell, "Run a shell command.", ToolCategory.SystemControl, DangerTier.Mutating,
            Reversibility.Irreversible,
            """{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}"""),
    ];

    public static string QualifiedName(string tool) => $"{SourcePrefix}/{tool}";

    static ToolDefinition Define(
        string name, string description, ToolCategory category, DangerTier tier, Reversibility reversibility, string schema) =>
        new(
            new ToolRegistration(name, Version, category, description),
            new ToolManifest
            {
                Name = name,
                Description = description,
                DangerTier = tier,
                Reversibility = reversibility,
                ArgsSchema = JsonDocument.Parse(schema).RootElement.Clone(),
            });
}

/// <summary>Hands the catalog to <see cref="ToolLoader"/>, which prefixes every name with the source prefix.</summary>
public sealed class CatalogToolSource : IToolSource
{
    public string SourcePrefix => ToolCatalog.SourcePrefix;

    public Task<IReadOnlyList<ToolSurvey>> SurveyAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ToolSurvey>>(ToolCatalog.All.Select(t => new ToolSurvey
        {
            Name = t.Manifest.Name,
            Description = t.Manifest.Description,
            DangerTier = t.Manifest.DangerTier,
            RequiresConfirmation = t.Manifest.RequiresConfirmation,
        }).ToArray());

    public Task<ToolManifest?> TryUnpackAsync(string toolName, CancellationToken ct = default) =>
        Task.FromResult(ToolCatalog.All.Select(t => t.Manifest).FirstOrDefault(m => m.Name == toolName));
}
