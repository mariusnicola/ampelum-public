using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SignalGrid.Ampelum.Agent.Contracts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SignalGrid.Ampelum.Agent.Yaml;

/// <summary>Outcome of compiling one workflow YAML document.</summary>
/// <param name="Success">Whether compilation produced a valid <paramref name="Artifact"/>.</param>
/// <param name="Artifact">The compiled artifact, present only when <paramref name="Success"/> is true.</param>
/// <param name="Errors">Every static validation error found. A compiler collects all of them rather than failing on the first, so an author can fix a document in one pass.</param>
public sealed record WorkflowCompilationResult(bool Success, WorkflowArtifact? Artifact, IReadOnlyList<string> Errors)
{
    internal static WorkflowCompilationResult Fail(IReadOnlyList<string> errors) => new(false, null, errors);
    internal static WorkflowCompilationResult Ok(WorkflowArtifact artifact) => new(true, artifact, []);
}

/// <summary>
/// The single official compiler from workflow YAML text to a content-addressed
/// <see cref="WorkflowArtifact"/>: parse YAML -> map to the canonical AST -> statically validate -> hash.
/// This is the only place workflow YAML should be parsed; <c>DurableWorkflowRuntime</c> executes the
/// resulting <see cref="WorkflowDefinition"/> and does not know YAML exists.
/// </summary>
public static class WorkflowYamlCompiler
{
    private static readonly IReadOnlySet<string> SupportedSchemaVersions = new HashSet<string>(StringComparer.Ordinal) { "1.0" };
    private static readonly JsonSerializerOptions HashJsonOptions = new() { WriteIndented = false };
    // Deliberately strict: an unrecognized top-level or step-level YAML key fails compilation rather than
    // silently being dropped. This is the "unknown fields fail loud" hard rule from the workflow-system spec --
    // IgnoreUnmatchedProperties() would otherwise let a typo like `dependes_on:` silently produce an empty
    // dependency list with no error.
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    /// <summary>
    /// Compiles <paramref name="yamlText"/>. Returns a failed result with every collected error rather than
    /// throwing, except for YAML that does not even parse (malformed syntax), which fails loud via a single
    /// collected error rather than propagating a raw parser exception to the caller.
    /// </summary>
    public static WorkflowCompilationResult Compile(string yamlText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlText);

        WorkflowYamlDto? dto;
        try
        {
            dto = Deserializer.Deserialize<WorkflowYamlDto>(yamlText);
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException)
        {
            return WorkflowCompilationResult.Fail([$"YAML did not parse: {ex.Message}"]);
        }

        if (dto is null)
            return WorkflowCompilationResult.Fail(["YAML document was empty."]);

        var errors = new List<string>();

        var schemaVersion = string.IsNullOrWhiteSpace(dto.SchemaVersion) ? "1.0" : dto.SchemaVersion;
        if (!SupportedSchemaVersions.Contains(schemaVersion))
            errors.Add($"Unknown schema version '{schemaVersion}'. Supported: {string.Join(", ", SupportedSchemaVersions)}.");

        if (string.IsNullOrWhiteSpace(dto.Id)) errors.Add("Workflow 'id' is required.");
        if (string.IsNullOrWhiteSpace(dto.Version)) errors.Add("Workflow 'version' is required.");
        if (dto.Steps is null || dto.Steps.Count == 0) errors.Add("Workflow must declare at least one step.");

        // Cannot safely continue mapping/validating steps without the basics above.
        if (errors.Count > 0) return WorkflowCompilationResult.Fail(errors);

        var steps = new List<WorkflowStepDefinition>();
        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stepDto in dto.Steps!)
        {
            if (string.IsNullOrWhiteSpace(stepDto.Id)) { errors.Add("A step is missing a non-empty 'id'."); continue; }
            if (!stepIds.Add(stepDto.Id)) { errors.Add($"Duplicate step id '{stepDto.Id}'."); continue; }

            if (!TryMapKind(stepDto.Kind, out var kind))
            {
                errors.Add($"Step '{stepDto.Id}' declares unknown kind '{stepDto.Kind}'.");
                continue;
            }

            AutonomyMode? autonomy = null;
            if (stepDto.Autonomy is not null)
            {
                if (!TryParseSnakeEnum<AutonomyMode>(stepDto.Autonomy, out var parsedAutonomy))
                {
                    errors.Add($"Step '{stepDto.Id}' declares unknown autonomy '{stepDto.Autonomy}'.");
                    continue;
                }
                autonomy = parsedAutonomy;
            }

            EffectClass? effect = null;
            if (stepDto.Effect is not null)
            {
                if (!TryParseSnakeEnum<EffectClass>(stepDto.Effect, out var parsedEffect))
                {
                    errors.Add($"Step '{stepDto.Id}' declares unknown effect '{stepDto.Effect}'.");
                    continue;
                }
                effect = parsedEffect;
            }

            WorkflowBudget? stepBudget = stepDto.MaxCost is null && stepDto.MaxTokens is null
                ? null
                : new WorkflowBudget(stepDto.MaxCost, stepDto.MaxTokens);
            if (stepBudget is not null && !ValidateBudget(stepBudget, $"Step '{stepDto.Id}'", errors))
                continue;

            if (stepDto.TimeoutSeconds is < 0)
                errors.Add($"Step '{stepDto.Id}' declares a negative timeout_seconds.");

            if ((effect is EffectClass.Mutating or EffectClass.Irreversible)
                && string.IsNullOrWhiteSpace(stepDto.CompensationStep)
                && string.IsNullOrWhiteSpace(stepDto.EffectJustification))
            {
                errors.Add($"Step '{stepDto.Id}' declares effect '{effect}' but neither a compensation step nor an explicit justification.");
            }

            var inputs = stepDto.Inputs;
            if (stepDto.Workers is { Count: > 0 })
            {
                inputs = new Dictionary<string, string>(inputs ?? new Dictionary<string, string>(), StringComparer.Ordinal);
                for (var i = 0; i < stepDto.Workers.Count; i++)
                    foreach (var (key, value) in stepDto.Workers[i])
                    {
                        var synthesizedKey = $"worker.{i}.{key}";
                        if (stepDto.Inputs?.ContainsKey(synthesizedKey) == true)
                        {
                            errors.Add($"Step '{stepDto.Id}' declares an 'inputs' entry '{synthesizedKey}' that collides with its 'workers' expansion.");
                            continue;
                        }
                        inputs[synthesizedKey] = value;
                    }
            }
            else if (kind == WorkflowStepKind.FanOut)
            {
                errors.Add($"Fan-out step '{stepDto.Id}' must declare at least one worker.");
            }

            // Canonicalize key order before this becomes part of a content-addressed artifact hash -- a
            // Dictionary<K,V>'s enumeration order is an undocumented implementation detail, not a guarantee,
            // and two semantically-identical documents that merely list keys in a different order must still
            // hash identically.
            var canonicalInputs = inputs is null
                ? null
                : new SortedDictionary<string, string>(inputs, StringComparer.Ordinal) as IReadOnlyDictionary<string, string>;

            // StepType carries the tool/task identifier a handler dispatches on (distinct from Kind, which
            // governs runtime traversal semantics only) -- it must never be the raw kind string, or every
            // Tool-kind step in a workflow would collapse onto the same identifier ("tool"), making a
            // declared ToolAllowList meaningless and any dispatcher call nonsensical.
            var stepType = stepDto.Tool ?? stepDto.Id;
            if (kind == WorkflowStepKind.Tool && stepDto.ToolAllowList is { Count: > 0 } allowList
                && !allowList.Contains(stepType, StringComparer.Ordinal))
                errors.Add($"Step '{stepDto.Id}' resolves to tool '{stepType}', which is not on its own declared tool_allow_list.");

            steps.Add(new WorkflowStepDefinition(
                StepId: stepDto.Id,
                StepType: stepType,
                DependsOn: stepDto.DependsOn ?? [],
                Inputs: canonicalInputs,
                Timeout: stepDto.TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                Kind: kind,
                BranchTargets: stepDto.BranchTargets,
                MaxIterations: stepDto.MaxIterations,
                ToolAllowList: stepDto.ToolAllowList,
                MaxAutonomy: autonomy,
                CompensationStepId: stepDto.CompensationStep,
                Effect: effect,
                Budget: stepBudget,
                EffectJustification: stepDto.EffectJustification));
        }

        foreach (var step in steps)
        {
            foreach (var dependency in step.DependsOn)
                if (!stepIds.Contains(dependency))
                    errors.Add($"Step '{step.StepId}' depends on unknown step '{dependency}'.");

            if (step.Kind == WorkflowStepKind.Branch)
            {
                if (step.BranchTargets is null || step.BranchTargets.Count == 0)
                    errors.Add($"Branch step '{step.StepId}' must declare at least one branch target.");
                else
                {
                    foreach (var target in step.BranchTargets)
                        if (!stepIds.Contains(target))
                            errors.Add($"Branch step '{step.StepId}' targets unknown step '{target}'.");

                    var hasExplicitJoin = steps.Any(candidate =>
                        candidate.Kind == WorkflowStepKind.Join
                        && step.BranchTargets.All(target => candidate.DependsOn.Contains(target, StringComparer.Ordinal)));
                    if (!hasExplicitJoin)
                        errors.Add($"Branch step '{step.StepId}' has no explicit join step reconverging all of its branch targets.");
                }
            }

            if (step.Kind == WorkflowStepKind.FanOut)
            {
                var hasJoin = steps.Any(candidate =>
                    candidate.Kind == WorkflowStepKind.Join && candidate.DependsOn.Contains(step.StepId, StringComparer.Ordinal));
                if (!hasJoin)
                    errors.Add($"Fan-out step '{step.StepId}' has no join step depending on it.");
            }

            if (step.Kind == WorkflowStepKind.Loop && step.MaxIterations is not (> 0))
                errors.Add($"Loop step '{step.StepId}' must declare a positive max_iterations bound.");
        }

        WorkflowObjective? objective = null;
        if (dto.Objective?.Metrics is { Count: > 0 } metrics)
        {
            var mapped = new List<WorkflowObjectiveMetric>();
            foreach (var metric in metrics)
            {
                if (string.IsNullOrWhiteSpace(metric.Id)) { errors.Add("An objective metric is missing 'id'."); continue; }
                if (metric.Direction is not ("maximize" or "minimize"))
                {
                    errors.Add($"Objective metric '{metric.Id}' declares direction '{metric.Direction}'; expected 'maximize' or 'minimize'.");
                    continue;
                }
                if (metric.Threshold is { } threshold && !double.IsFinite(threshold))
                {
                    errors.Add($"Objective metric '{metric.Id}' declares a threshold that is not a finite number.");
                    continue;
                }
                mapped.Add(new WorkflowObjectiveMetric(metric.Id, metric.Direction, metric.Threshold));
            }
            objective = new WorkflowObjective(mapped);
        }

        WorkflowEvaluationPlan? evaluationPlan = null;
        if (dto.Evaluation is { } evaluation)
        {
            var planned = new List<string>();
            foreach (var id in evaluation.Metrics ?? [])
            {
                if (string.IsNullOrWhiteSpace(id)) { errors.Add("The evaluation section lists a blank metric id."); continue; }
                if (planned.Contains(id, StringComparer.Ordinal)) { errors.Add($"The evaluation section lists metric '{id}' more than once."); continue; }
                if (objective is null || !objective.Metrics.Any(metric => metric.MetricId == id))
                {
                    errors.Add($"The evaluation section lists metric '{id}', which the objective does not declare.");
                    continue;
                }
                planned.Add(id);
            }
            if ((evaluation.Metrics ?? []).Count == 0) errors.Add("The evaluation section must list at least one metric.");
            evaluationPlan = new WorkflowEvaluationPlan(planned, evaluation.Enabled ?? true);
        }

        WorkflowPolicyBinding? policy = null;
        if (dto.Risk is not null)
        {
            if (!TryParseSnakeEnum<EffectClass>(dto.Risk.MaxEffectClass, out var maxEffectClass))
            {
                errors.Add($"risk.max_effect_class declares unknown value '{dto.Risk.MaxEffectClass}'.");
            }
            else
            {
                IReadOnlyList<EffectClass>? requireApprovalFor = null;
                if (dto.Risk.RequireApprovalFor is { Count: > 0 } raw)
                {
                    var parsedList = new List<EffectClass>();
                    foreach (var value in raw)
                    {
                        if (!TryParseSnakeEnum<EffectClass>(value, out var parsed))
                        {
                            errors.Add($"risk.require_approval_for declares unknown value '{value}'.");
                            continue;
                        }
                        parsedList.Add(parsed);
                    }
                    requireApprovalFor = parsedList;
                }
                var budget = dto.Risk.MaxCost is null && dto.Risk.MaxTokens is null && dto.Risk.MaxDurationSeconds is null
                    ? null
                    : new WorkflowBudget(dto.Risk.MaxCost, dto.Risk.MaxTokens, dto.Risk.MaxDurationSeconds is { } s ? TimeSpan.FromSeconds(s) : null);
                if (budget is not null) ValidateBudget(budget, "risk", errors);
                policy = new WorkflowPolicyBinding(maxEffectClass, requireApprovalFor, budget);
            }
        }

        List<WorkflowContextRequest>? contextRequests = null;
        if (dto.Context?.Required is { Count: > 0 } required)
        {
            contextRequests = [];
            foreach (var entry in required)
                foreach (var (kind, identifier) in entry.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    contextRequests.Add(new WorkflowContextRequest(kind, identifier));
        }

        List<WorkflowTriggerDefinition>? triggers = null;
        if (dto.Triggers is { Count: > 0 })
        {
            triggers = [];
            foreach (var trigger in dto.Triggers)
            {
                if (string.IsNullOrWhiteSpace(trigger.Type)) { errors.Add("A trigger is missing 'type'."); continue; }
                var config = trigger.Config is null ? null : new SortedDictionary<string, string>(trigger.Config, StringComparer.Ordinal) as IReadOnlyDictionary<string, string>;
                triggers.Add(new WorkflowTriggerDefinition(trigger.Type, config));
            }
        }

        List<WorkflowInputDefinition>? inputDefinitions = dto.Inputs?
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Select(i => new WorkflowInputDefinition(i.Name!, i.SchemaRef, i.Required ?? true))
            .ToList();

        List<WorkflowOutputDefinition>? outputDefinitions = dto.Outputs?
            .Where(o => !string.IsNullOrWhiteSpace(o.Name))
            .Select(o => new WorkflowOutputDefinition(o.Name!, o.SchemaRef))
            .ToList();

        var optimization = dto.Optimization is null ? null : new WorkflowOptimizationPolicy(dto.Optimization.Enabled, dto.Optimization.Promotion);
        IReadOnlyDictionary<string, string>? metadata = string.IsNullOrWhiteSpace(dto.Description)
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal) { ["description"] = dto.Description };

        if (errors.Count > 0) return WorkflowCompilationResult.Fail(errors);

        var definition = new WorkflowDefinition(
            WorkflowId: dto.Id!,
            Version: dto.Version!,
            Name: string.IsNullOrWhiteSpace(dto.Name) ? dto.Id! : dto.Name,
            Steps: steps,
            Metadata: metadata,
            Objective: objective,
            Policy: policy,
            ContextRequests: contextRequests,
            EvaluationPlan: evaluationPlan,
            Optimization: optimization,
            Inputs: inputDefinitions,
            Outputs: outputDefinitions,
            Triggers: triggers);

        var schemaHash = Hash(schemaVersion);
        var policyHash = Hash(policy is null ? "" : JsonSerializer.Serialize(policy, HashJsonOptions));
        var artifactHash = Hash(JsonSerializer.Serialize(definition, HashJsonOptions));

        return WorkflowCompilationResult.Ok(new WorkflowArtifact(artifactHash, schemaHash, policyHash, definition));
    }

    private static bool ValidateBudget(WorkflowBudget budget, string owner, List<string> errors)
    {
        var valid = true;
        if (budget.MaxCost is < 0) { errors.Add($"{owner} declares a negative max_cost."); valid = false; }
        if (budget.MaxTokens is < 0) { errors.Add($"{owner} declares a negative max_tokens."); valid = false; }
        if (budget.MaxDuration is { } duration && duration < TimeSpan.Zero) { errors.Add($"{owner} declares a negative max duration."); valid = false; }
        if (budget.MaxCost is null && budget.MaxTokens is null && budget.MaxDuration is null) { errors.Add($"{owner} declares a budget with no limit set."); valid = false; }
        return valid;
    }

    private static bool TryMapKind(string? raw, out WorkflowStepKind kind)
    {
        kind = WorkflowStepKind.Sequential;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        return TryParseSnakeEnum(raw, out kind);
    }

    /// <summary>Parses a snake_case YAML scalar (e.g. <c>fan_out</c>, <c>require_approval</c>) against a PascalCase enum member (e.g. <see cref="WorkflowStepKind.FanOut"/>).</summary>
    private static bool TryParseSnakeEnum<TEnum>(string? raw, out TEnum value) where TEnum : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var normalized = raw.Replace("_", "", StringComparison.Ordinal);
        foreach (var candidate in Enum.GetValues<TEnum>())
            if (string.Equals(candidate.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        return false;
    }

    private static string Hash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
