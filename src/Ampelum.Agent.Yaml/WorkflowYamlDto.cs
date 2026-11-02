namespace SignalGrid.Ampelum.Agent.Yaml;

/// <summary>Raw YAML deserialization target for a workflow document. Never exposed publicly — the
/// compiler's only public output is a validated <c>SignalGrid.Ampelum.Agent.Contracts.WorkflowArtifact</c>.</summary>
internal sealed class WorkflowYamlDto
{
    public string? SchemaVersion { get; set; }
    public string? Id { get; set; }
    public string? Version { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public ObjectiveYamlDto? Objective { get; set; }
    public RiskYamlDto? Risk { get; set; }
    public ContextYamlDto? Context { get; set; }
    public List<TriggerYamlDto>? Triggers { get; set; }
    public List<StepYamlDto>? Steps { get; set; }
    public OptimizationYamlDto? Optimization { get; set; }
    public List<InputYamlDto>? Inputs { get; set; }
    public List<OutputYamlDto>? Outputs { get; set; }
}

internal sealed class ObjectiveYamlDto
{
    public List<MetricYamlDto>? Metrics { get; set; }
}

internal sealed class MetricYamlDto
{
    public string? Id { get; set; }
    public string? Direction { get; set; }
}

internal sealed class RiskYamlDto
{
    public string? MaxEffectClass { get; set; }
    public decimal? MaxCost { get; set; }
    public int? MaxTokens { get; set; }
    public double? MaxDurationSeconds { get; set; }
    public List<string>? RequireApprovalFor { get; set; }
}

internal sealed class ContextYamlDto
{
    public List<Dictionary<string, string>>? Required { get; set; }
}

internal sealed class TriggerYamlDto
{
    public string? Type { get; set; }
    public Dictionary<string, string>? Config { get; set; }
}

internal sealed class InputYamlDto
{
    public string? Name { get; set; }
    public string? SchemaRef { get; set; }
    public bool? Required { get; set; }
}

internal sealed class OutputYamlDto
{
    public string? Name { get; set; }
    public string? SchemaRef { get; set; }
}

internal sealed class StepYamlDto
{
    public string? Id { get; set; }
    public string? Kind { get; set; }
    public string? Tool { get; set; }
    public List<string>? DependsOn { get; set; }
    public List<string>? BranchTargets { get; set; }
    public int? MaxIterations { get; set; }
    public string? Autonomy { get; set; }
    public string? Effect { get; set; }
    public List<string>? ToolAllowList { get; set; }
    public string? CompensationStep { get; set; }
    public string? EffectJustification { get; set; }
    public List<Dictionary<string, string>>? Workers { get; set; }
    public double? TimeoutSeconds { get; set; }
    public decimal? MaxCost { get; set; }
    public int? MaxTokens { get; set; }
    public Dictionary<string, string>? Inputs { get; set; }
}

internal sealed class OptimizationYamlDto
{
    public bool Enabled { get; set; }
    public string? Promotion { get; set; }
}
