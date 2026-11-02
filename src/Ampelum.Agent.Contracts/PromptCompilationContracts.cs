using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SignalGrid.Ampelum.Agent.Contracts;

/// <summary>Declared primitive type for a prompt input field.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public enum PromptInputKind
{
    /// <summary>Arbitrary text.</summary>
    Text,
    /// <summary>A signed 64-bit integer.</summary>
    Integer,
    /// <summary>A culture-invariant decimal value.</summary>
    Decimal,
    /// <summary>A Boolean value.</summary>
    Boolean,
    /// <summary>A complete JSON value.</summary>
    Json,
}

/// <summary>Typed, versioned declaration for one template input.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record PromptInputSchema(
    string Name,
    PromptInputKind Kind,
    bool Required = true,
    bool Secret = false,
    string? Description = null);

/// <summary>A typed value supplied during prompt compilation.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record PromptInputValue(string Name, PromptInputKind Kind, string Value);

/// <summary>Versioned tool-policy identity included in a compiled prompt's reproducibility boundary.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record PromptToolPolicyBinding(
    string PolicyId,
    string Version,
    IReadOnlyList<string> AllowedToolIds,
    ToolPolicyDisposition DefaultDisposition = ToolPolicyDisposition.Block);

/// <summary>Immutable source definition for a versioned prompt template.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record PromptTemplateDefinition(
    string TemplateId,
    string Version,
    string Template,
    IReadOnlyList<PromptInputSchema> InputSchema,
    PromptToolPolicyBinding ToolPolicy);

/// <summary>All inputs required to compile one prompt artifact.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record PromptCompilationRequest(
    PromptTemplateDefinition Definition,
    IReadOnlyList<PromptInputValue> Inputs);

/// <summary>
/// Immutable, deterministic result of compiling a prompt definition and its typed values.
/// The content hash covers the template, version, schema, tool policy, and all values; audit
/// records deliberately expose only the hash and never the rendered content or input values.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record CompiledPromptArtifact(
    string ArtifactHash,
    string TemplateId,
    string TemplateVersion,
    string RenderedPrompt,
    string InputSchemaHash,
    string ToolPolicyHash,
    IReadOnlyList<string> InputNames);

/// <summary>Secret-safe identifiers that connect a prompt artifact, evaluation, audit event, and replay.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record PromptArtifactCorrelation(
    string ArtifactHash,
    string EvaluationId,
    string ExecutionId,
    string ReplayCorrelationId);

/// <summary>One item a context manifest declined to include, with a human-readable reason.</summary>
/// <param name="ItemId">Identifier of the excluded item.</param>
/// <param name="Reason">Why it was excluded (e.g. expired, out of scope, missing permission).</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record PromptContextExclusion(string ItemId, string Reason);

/// <summary>
/// Budget remaining per dimension, computed only for dimensions the caller's <c>WorkflowBudget</c> actually
/// declared. Each field is <c>null</c> when that dimension has no declared limit — distinct from a
/// concrete <c>0</c>, which means the declared limit for that dimension is fully exhausted. Deliberately not
/// <c>WorkflowBudgetUsage</c> (whose fields are non-nullable "usage so far" semantics): collapsing "no limit
/// declared" and "limit exhausted" onto the same zero would silently hide which constraints are even in
/// effect, which is exactly what this manifest exists to make explicit.
/// </summary>
/// <param name="Cost">Cost remaining, or <c>null</c> if no cost budget was declared.</param>
/// <param name="Tokens">Tokens remaining, or <c>null</c> if no token budget was declared.</param>
/// <param name="Duration">Duration remaining, or <c>null</c> if no duration budget was declared.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record PromptContextRemainingBudget(decimal? Cost, int? Tokens, TimeSpan? Duration);

/// <summary>
/// An auditable record of exactly what context a prompt compilation drew on: which evidence and knowledge
/// were actually included, which policies and tools were in effect, how much budget remained, and — just as
/// important — what was excluded and why. Exists specifically so a compiled prompt never silently includes
/// "everything available"; every inclusion and exclusion is an explicit, inspectable decision.
/// </summary>
/// <param name="IncludedEvidenceIds">Evidence records included after scope/permission/expiry filtering.</param>
/// <param name="IncludedKnowledgeIds">Knowledge item identifiers included.</param>
/// <param name="IncludedPolicyIds">Policy identifiers in effect for this compilation.</param>
/// <param name="AvailableToolIds">Tool identifiers available to whatever this context is compiled for.</param>
/// <param name="RemainingBudget">Budget remaining after already-incurred usage is subtracted, if a budget was declared.</param>
/// <param name="Excluded">Every item considered but not included, with its reason.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record PromptContextManifest(
    IReadOnlyList<string> IncludedEvidenceIds,
    IReadOnlyList<string> IncludedKnowledgeIds,
    IReadOnlyList<string> IncludedPolicyIds,
    IReadOnlyList<string> AvailableToolIds,
    PromptContextRemainingBudget? RemainingBudget,
    IReadOnlyList<PromptContextExclusion> Excluded);

/// <summary>Compiles typed prompt definitions into deterministic, versioned artifacts.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class PromptCompiler
{
    /// <summary>Compiles one request and freezes all collection-derived output.</summary>
    public static CompiledPromptArtifact Compile(PromptCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Definition);
        ArgumentNullException.ThrowIfNull(request.Definition.InputSchema);
        ArgumentNullException.ThrowIfNull(request.Definition.ToolPolicy);
        ArgumentNullException.ThrowIfNull(request.Definition.ToolPolicy.AllowedToolIds);
        ArgumentNullException.ThrowIfNull(request.Inputs);
        Require(request.Definition.TemplateId, nameof(request.Definition.TemplateId));
        Require(request.Definition.Version, nameof(request.Definition.Version));
        Require(request.Definition.Template, nameof(request.Definition.Template));
        Require(request.Definition.ToolPolicy.PolicyId, nameof(request.Definition.ToolPolicy.PolicyId));
        Require(request.Definition.ToolPolicy.Version, nameof(request.Definition.ToolPolicy.Version));
        var toolIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var toolId in request.Definition.ToolPolicy.AllowedToolIds)
        {
            Require(toolId, "AllowedToolIds");
            if (!toolIds.Add(toolId))
                throw new ArgumentException($"Duplicate allowed tool '{toolId}'.", nameof(request));
        }

        var schema = request.Definition.InputSchema.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        var inputs = request.Inputs.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        Validate(schema, inputs);

        var schemaHash = Hash(CanonicalSchema(schema));
        var policyHash = Hash(CanonicalPolicy(request.Definition.ToolPolicy));
        var artifactHash = Hash(CanonicalArtifact(request.Definition, schema, inputs, schemaHash, policyHash));
        var rendered = Render(request.Definition.Template, inputs);

        return new CompiledPromptArtifact(
            artifactHash,
            request.Definition.TemplateId,
            request.Definition.Version,
            rendered,
            schemaHash,
            policyHash,
            new ReadOnlyCollection<string>(inputs.Select(x => x.Name).ToArray()));
    }

    /// <summary>Builds an evaluator request whose evidence contains only reproducible, non-secret identifiers.</summary>
    public static EvaluationRequest CreateEvaluationRequest(
        CompiledPromptArtifact artifact,
        string evaluationId,
        string executionId,
        string evaluatorId,
        DateTimeOffset requestedAt,
        string replayCorrelationId)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        Require(evaluationId, nameof(evaluationId));
        Require(executionId, nameof(executionId));
        Require(evaluatorId, nameof(evaluatorId));
        Require(replayCorrelationId, nameof(replayCorrelationId));

        return new EvaluationRequest(
            evaluationId,
            executionId,
            evaluatorId,
            requestedAt,
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["prompt.artifact.hash"] = artifact.ArtifactHash,
                ["prompt.schema.hash"] = artifact.InputSchemaHash,
                ["prompt.tool-policy.hash"] = artifact.ToolPolicyHash,
                ["prompt.replay.correlation"] = replayCorrelationId,
            }));
    }

    /// <summary>Creates an audit event correlated to the artifact and evaluator without copying any prompt input values.</summary>
    public static AgentAuditEvent CreateAuditEvent(
        CompiledPromptArtifact artifact,
        EvaluationRequest evaluation,
        string replayCorrelationId,
        string eventId,
        string actorId,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(evaluation);
        Require(replayCorrelationId, nameof(replayCorrelationId));
        Require(eventId, nameof(eventId));
        Require(actorId, nameof(actorId));

        return new AgentAuditEvent(
            eventId,
            "prompt.compiled",
            evaluation.ExecutionId,
            occurredAt,
            actorId,
            replayCorrelationId,
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["prompt.artifact.hash"] = artifact.ArtifactHash,
                ["prompt.schema.hash"] = artifact.InputSchemaHash,
                ["prompt.tool-policy.hash"] = artifact.ToolPolicyHash,
                ["prompt.evaluation.id"] = evaluation.EvaluationId,
                ["prompt.evaluator.id"] = evaluation.EvaluatorId,
            }));
    }

    /// <summary>
    /// Builds an auditable manifest of exactly what context is available for a prompt: filters
    /// <paramref name="availableEvidence"/> by expiry, scope, and permission, records why anything was
    /// excluded, and reports remaining budget after <paramref name="usageSoFar"/> is subtracted. Never
    /// includes evidence the caller isn't allowed to see, even if the caller never asked for it explicitly —
    /// every exclusion is recorded, not silently dropped.
    /// </summary>
    public static PromptContextManifest BuildContextManifest(
        IReadOnlyList<EvidenceRecord> availableEvidence,
        IReadOnlyList<string> availableKnowledgeIds,
        IReadOnlyList<string> callerScopes,
        IReadOnlyList<string> callerPermissions,
        IReadOnlyList<string> policyIds,
        IReadOnlyList<string> availableToolIds,
        WorkflowBudget? budget,
        WorkflowBudgetUsage? usageSoFar,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(availableEvidence);
        ArgumentNullException.ThrowIfNull(availableKnowledgeIds);
        ArgumentNullException.ThrowIfNull(callerScopes);
        ArgumentNullException.ThrowIfNull(callerPermissions);
        ArgumentNullException.ThrowIfNull(policyIds);
        ArgumentNullException.ThrowIfNull(availableToolIds);

        var included = new List<string>();
        var excluded = new List<PromptContextExclusion>();
        foreach (var evidence in availableEvidence)
        {
            if (evidence.ValidUntil is { } validUntil && validUntil < now)
            {
                excluded.Add(new PromptContextExclusion(evidence.EvidenceId, $"expired at {validUntil:O}"));
                continue;
            }
            if (evidence.Scope is { } scope && !callerScopes.Contains(scope, StringComparer.Ordinal))
            {
                excluded.Add(new PromptContextExclusion(evidence.EvidenceId, $"scope '{scope}' is not among the caller's scopes"));
                continue;
            }
            if (evidence.Permissions is { Count: > 0 } required && !required.All(p => callerPermissions.Contains(p, StringComparer.Ordinal)))
            {
                excluded.Add(new PromptContextExclusion(evidence.EvidenceId, $"caller is missing required permission(s): {string.Join(", ", required.Where(p => !callerPermissions.Contains(p, StringComparer.Ordinal)))}"));
                continue;
            }
            included.Add(evidence.EvidenceId);
        }

        PromptContextRemainingBudget? remaining = budget is null
            ? null
            : new PromptContextRemainingBudget(
                Cost: budget.MaxCost is { } maxCost ? Math.Max(0m, maxCost - (usageSoFar?.Cost ?? 0m)) : null,
                Tokens: budget.MaxTokens is { } maxTokens ? Math.Max(0, maxTokens - (usageSoFar?.Tokens ?? 0)) : null,
                Duration: budget.MaxDuration is { } maxDuration
                    ? (maxDuration - (usageSoFar?.Duration ?? TimeSpan.Zero) is { } left && left > TimeSpan.Zero ? left : TimeSpan.Zero)
                    : null);

        return new PromptContextManifest(included, [.. availableKnowledgeIds], [.. policyIds], [.. availableToolIds], remaining, excluded);
    }

    /// <summary>Returns the common reproducibility tuple after evaluator and audit requests are created.</summary>
    public static PromptArtifactCorrelation CreateCorrelation(
        CompiledPromptArtifact artifact,
        EvaluationRequest evaluation,
        string replayCorrelationId)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(evaluation);
        Require(replayCorrelationId, nameof(replayCorrelationId));
        return new PromptArtifactCorrelation(artifact.ArtifactHash, evaluation.EvaluationId, evaluation.ExecutionId, replayCorrelationId);
    }

    static void Validate(IReadOnlyList<PromptInputSchema> schema, IReadOnlyList<PromptInputValue> inputs)
    {
        var schemaByName = new Dictionary<string, PromptInputSchema>(StringComparer.Ordinal);
        foreach (var field in schema)
        {
            ArgumentNullException.ThrowIfNull(field);
            Require(field.Name, nameof(field.Name));
            if (!schemaByName.TryAdd(field.Name, field))
                throw new ArgumentException($"Duplicate prompt schema field '{field.Name}'.", nameof(schema));
        }

        var valuesByName = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in inputs)
        {
            ArgumentNullException.ThrowIfNull(value);
            Require(value.Name, nameof(value.Name));
            ArgumentNullException.ThrowIfNull(value.Value);
            if (!valuesByName.Add(value.Name))
                throw new ArgumentException($"Duplicate prompt input '{value.Name}'.", nameof(inputs));
            if (!schemaByName.TryGetValue(value.Name, out var field))
                throw new ArgumentException($"Prompt input '{value.Name}' is not declared by the schema.", nameof(inputs));
            if (field.Kind != value.Kind)
                throw new ArgumentException($"Prompt input '{value.Name}' does not match declared kind '{field.Kind}'.", nameof(inputs));
            ValidateValue(value);
        }

        foreach (var field in schemaByName.Values.Where(x => x.Required))
            if (!valuesByName.Contains(field.Name))
                throw new ArgumentException($"Required prompt input '{field.Name}' is missing.", nameof(inputs));
    }

    static void ValidateValue(PromptInputValue value)
    {
        var valid = value.Kind switch
        {
            PromptInputKind.Text => true,
            PromptInputKind.Integer => long.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            PromptInputKind.Decimal => decimal.TryParse(value.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out _),
            PromptInputKind.Boolean => bool.TryParse(value.Value, out _),
            PromptInputKind.Json => IsJson(value.Value),
            _ => false,
        };
        if (!valid)
            throw new ArgumentException($"Prompt input '{value.Name}' is not a valid {value.Kind} value.", nameof(value));
    }

    static bool IsJson(string value)
    {
        try { using var _ = JsonDocument.Parse(value); return true; }
        catch (JsonException) { return false; }
    }

    static string Render(string template, IEnumerable<PromptInputValue> inputs)
    {
        var rendered = template;
        foreach (var value in inputs)
            rendered = rendered.Replace("{{" + value.Name + "}}", value.Value, StringComparison.Ordinal);
        return rendered;
    }

    static string CanonicalSchema(IEnumerable<PromptInputSchema> schema)
        => string.Concat(schema.Select(x => Join("field", x.Name, ((int)x.Kind).ToString(CultureInfo.InvariantCulture), x.Required ? "1" : "0", x.Secret ? "1" : "0", x.Description ?? string.Empty)));

    static string CanonicalPolicy(PromptToolPolicyBinding policy)
        => Join("policy", policy.PolicyId, policy.Version, ((int)policy.DefaultDisposition).ToString(CultureInfo.InvariantCulture)) + string.Concat(policy.AllowedToolIds.OrderBy(x => x, StringComparer.Ordinal).Select(x => Join("tool", x)));

    static string CanonicalArtifact(PromptTemplateDefinition definition, IEnumerable<PromptInputSchema> schema, IEnumerable<PromptInputValue> inputs, string schemaHash, string policyHash)
        => Join("artifact-v1", definition.TemplateId, definition.Version, definition.Template, schemaHash, policyHash)
           + CanonicalSchema(schema)
           + string.Concat(inputs.Select(x => Join("input", x.Name, ((int)x.Kind).ToString(CultureInfo.InvariantCulture), x.Value)));

    static string Join(params string[] values)
        => string.Concat(values.Select(x => $"{x.Length}:{x}"));

    static string Hash(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    static void Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value must not be blank.", parameterName);
    }
}
