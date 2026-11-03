using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Agent.Contracts;

/// <summary>A named, schema-referencing input a workflow accepts at the start of execution.</summary>
/// <param name="Name">The input name callers bind a value to in <see cref="WorkflowExecutionRequest.Inputs"/>.</param>
/// <param name="SchemaRef">Optional identifier of a schema the bound value must satisfy.</param>
/// <param name="Required">Whether execution may start without this input bound.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowInputDefinition(string Name, string? SchemaRef = null, bool Required = true);

/// <summary>A named, schema-referencing output a workflow produces once it reaches a terminal state.</summary>
/// <param name="Name">The output name.</param>
/// <param name="SchemaRef">Optional identifier of a schema the produced value must satisfy.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowOutputDefinition(string Name, string? SchemaRef = null);

/// <summary>One declared way a workflow may be started.</summary>
/// <param name="Type">The trigger discriminator (e.g. <c>"manual"</c>, <c>"schedule"</c>, <c>"event"</c>); interpretation belongs to the host, not this record.</param>
/// <param name="Config">Free-form string configuration for this trigger (e.g. a schedule expression).</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowTriggerDefinition(string Type, IReadOnlyDictionary<string, string>? Config = null);

/// <summary>Declarative, runtime-neutral definition of an agent workflow.</summary>
/// <param name="WorkflowId">Stable identifier for this workflow across versions.</param>
/// <param name="Version">This definition's own version string.</param>
/// <param name="Name">Human-readable name.</param>
/// <param name="Steps">The workflow's steps.</param>
/// <param name="Metadata">Free-form string metadata.</param>
/// <param name="Objective">The measurable goal this workflow is optimized against.</param>
/// <param name="Policy">Risk/policy constraints binding this workflow's autonomy.</param>
/// <param name="ContextRequests">Evidence/knowledge this workflow declares it needs before it can run.</param>
/// <param name="EvaluationPlan">This workflow's plan for evaluating its own outcomes.</param>
/// <param name="Optimization">This workflow's policy for generating and promoting improved versions of itself.</param>
/// <param name="Inputs">Named inputs this workflow accepts at the start of execution.</param>
/// <param name="Outputs">Named outputs this workflow produces at a terminal state.</param>
/// <param name="Triggers">Declared ways this workflow may be started.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowDefinition(
    string WorkflowId,
    string Version,
    string Name,
    IReadOnlyList<WorkflowStepDefinition> Steps,
    IReadOnlyDictionary<string, string>? Metadata = null,
    WorkflowObjective? Objective = null,
    WorkflowPolicyBinding? Policy = null,
    IReadOnlyList<WorkflowContextRequest>? ContextRequests = null,
    WorkflowEvaluationPlan? EvaluationPlan = null,
    WorkflowOptimizationPolicy? Optimization = null,
    IReadOnlyList<WorkflowInputDefinition>? Inputs = null,
    IReadOnlyList<WorkflowOutputDefinition>? Outputs = null,
    IReadOnlyList<WorkflowTriggerDefinition>? Triggers = null);

/// <summary>Typed vocabulary for the execution semantics of a workflow step.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public enum WorkflowStepKind
{
    /// <summary>Executes once; ready only once every declared dependency has succeeded.</summary>
    Sequential,
    /// <summary>Evaluates a typed condition input and executes exactly one of its declared branch targets.</summary>
    Branch,
    /// <summary>Re-executes its body until a typed exit condition is met or a declared maximum iteration count is reached.</summary>
    Loop,
    /// <summary>Invokes a named tool through the runtime's tool dispatcher.</summary>
    Tool,
    /// <summary>Pauses execution pending an explicit human or system approval.</summary>
    Approval,
    /// <summary>Pauses execution for a declared duration or external signal.</summary>
    Wait,
    /// <summary>
    /// Executed like <see cref="Tool"/>/<see cref="Sequential"/> — dispatched once to the runtime's generic
    /// handler — but declares an intent to fan work out internally (e.g. the handler awaits several
    /// concurrent sub-tasks itself). <c>DurableWorkflowRuntime</c> does not track per-worker state as
    /// separate durable nodes; parallelism within a <c>FanOut</c> step is the handler's responsibility, not
    /// the runtime's claim loop. A declared worker set is carried as step metadata by the compiler, not as
    /// separate graph nodes — see <c>Ampelum.Agent.Yaml</c>'s <c>workers:</c> expansion.
    /// </summary>
    FanOut,
    /// <summary>
    /// An ordinary step, executed like <see cref="Tool"/>/<see cref="Sequential"/>, that by convention
    /// depends on a <see cref="FanOut"/> step's id — the runtime's existing dependency-readiness check
    /// already reconverges naturally once that dependency succeeds, so this kind needs no dedicated runtime
    /// logic. <c>Ampelum.Agent.Yaml</c> enforces the FanOut-has-a-Join / Branch-has-a-Join authoring
    /// convention at compile time; the runtime itself does not require it (so hand-constructed
    /// <see cref="WorkflowDefinition"/> instances predating this convention are unaffected).
    /// </summary>
    Join,
    /// <summary>
    /// Pauses durably pending an external signal delivered out-of-band from any declared dependency's
    /// completion. <c>DurableWorkflowRuntime</c> never calls the handler for it: once ready it waits, and
    /// <c>SignalAsync</c> settles it with the signal named by <c>Inputs["signal"]</c> (the step id when absent).
    /// A signal that arrives earlier is kept on the node and the step succeeds as soon as it is ready.
    /// </summary>
    WaitForSignal,
    /// <summary>
    /// Observes the metrics of the workflow's <c>WorkflowEvaluationPlan</c> through the runtime's evaluator and
    /// records the observations. The step fails when a plan metric is missing or misses the threshold the workflow
    /// <c>Objective</c> gives it. Needs an enabled evaluation plan and a runtime with an evaluator.
    /// </summary>
    Evaluate,
    /// <summary>
    /// Appends a caller-declared event to the runtime's audit stream. <c>DurableWorkflowRuntime</c> never calls the
    /// handler for it: the event name is <c>Inputs["event"]</c> (required), the detail is <c>Inputs["detail"]</c>, and
    /// the audit event <c>event.&lt;name&gt;</c> is written by the same swap that marks the step succeeded.
    /// </summary>
    EmitEvent,
    /// <summary>
    /// Reverses a prior step's effect as part of saga-style rollback. A step names it in
    /// <c>CompensationStepId</c>. It never runs in the forward flow: when the execution would fail,
    /// <c>DurableWorkflowRuntime</c> runs, through the handler, the compensation of every succeeded step that declares
    /// one, newest completion first, and then fails the execution. A successful execution ends with it skipped.
    /// </summary>
    Compensate,
}

/// <summary>Declared maximum autonomy a workflow step or workflow may exercise without a human in the loop.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public enum AutonomyMode
{
    /// <summary>The step or workflow must never execute automatically.</summary>
    Forbidden,
    /// <summary>The step or workflow may only propose an action; nothing executes without a separate, explicit trigger.</summary>
    ProposeOnly,
    /// <summary>The step or workflow may execute against a simulated or replay environment, never a live one.</summary>
    Simulate,
    /// <summary>The step or workflow pauses for an authenticated approval before it executes against a live environment.</summary>
    RequireApproval,
    /// <summary>The step or workflow executes automatically and notifies observers after the fact.</summary>
    ExecuteAndNotify,
    /// <summary>The step or workflow executes automatically as long as it stays within its declared <see cref="WorkflowPolicyBinding"/> and <see cref="WorkflowBudget"/>.</summary>
    AutomaticWithinPolicy,
}

/// <summary>Declared reversibility/blast-radius classification of a step's external effect.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public enum EffectClass
{
    /// <summary>The step reads external state and changes nothing.</summary>
    ReadOnly,
    /// <summary>The step creates new state without altering or removing existing state.</summary>
    Additive,
    /// <summary>The step alters or removes existing external state.</summary>
    Mutating,
    /// <summary>The step's effect cannot be undone by a compensating action.</summary>
    Irreversible,
}

/// <summary>Lifecycle state of a synthesized or authored workflow artifact.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public enum WorkflowPromotionState
{
    /// <summary>The artifact is an unreviewed candidate.</summary>
    Draft,
    /// <summary>The artifact has been proposed for review.</summary>
    Proposed,
    /// <summary>The artifact has passed review but is not yet active.</summary>
    Reviewed,
    /// <summary>The artifact is the current canonical version for its workflow id.</summary>
    Active,
    /// <summary>The artifact was active and has been replaced by a newer version.</summary>
    Superseded,
    /// <summary>The artifact was promoted and then rolled back.</summary>
    RolledBack,
    /// <summary>The artifact is no longer eligible for promotion or execution.</summary>
    Retired,
}

/// <summary>A named, schema-referencing input a step consumes from a prior step's output or a workflow input.</summary>
/// <param name="Name">The parameter name the step's handler binds this value to.</param>
/// <param name="Source">
/// An expression identifying where the value comes from (e.g. a prior step id and output name, or a
/// workflow input name). Expression syntax and evaluation belong to the runtime/compiler, not this record.
/// </param>
/// <param name="SchemaRef">Optional identifier of a schema the bound value must satisfy.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowStepInputBinding(string Name, string Source, string? SchemaRef = null);

/// <summary>A named, schema-referencing output slot a step produces.</summary>
/// <param name="Name">The output slot name other steps can reference as a <see cref="WorkflowStepInputBinding.Source"/>.</param>
/// <param name="SchemaRef">Optional identifier of a schema the produced value must satisfy.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowStepOutputBinding(string Name, string? SchemaRef = null);

/// <summary>Declared token/cost/time budget for a workflow or one of its steps.</summary>
/// <param name="MaxCost">Maximum spend, in the caller's chosen currency/unit.</param>
/// <param name="MaxTokens">Maximum LLM token consumption.</param>
/// <param name="MaxDuration">Maximum wall-clock duration.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowBudget(decimal? MaxCost = null, int? MaxTokens = null, TimeSpan? MaxDuration = null);

/// <summary>Cumulative spend recorded so far against a workflow or step's declared <see cref="WorkflowBudget"/>.</summary>
/// <param name="Cost">Cumulative spend so far, in the same unit as <see cref="WorkflowBudget.MaxCost"/>.</param>
/// <param name="Tokens">Cumulative LLM token consumption so far.</param>
/// <param name="Duration">Cumulative wall-clock duration consumed so far.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowBudgetUsage(decimal Cost = 0, int Tokens = 0, TimeSpan Duration = default);

/// <summary>One dependency-addressable step in a declarative workflow.</summary>
/// <param name="StepId">Unique, non-empty identifier for this step within its workflow.</param>
/// <param name="StepType">Free-form discriminator a use case's handler dispatch may switch on; distinct from <paramref name="Kind"/>, which governs runtime traversal semantics.</param>
/// <param name="DependsOn">Step identifiers that must reach <c>Succeeded</c> before this step becomes ready.</param>
/// <param name="Inputs">Untyped string-keyed input map. Superseded by, but kept alongside, <paramref name="TypedInputs"/> for backward compatibility — existing consumers of this field are unaffected by the additions below.</param>
/// <param name="Timeout">Maximum duration the runtime allows one execution attempt of this step.</param>
/// <param name="Kind">The step's runtime traversal semantics. See <see cref="WorkflowStepKind"/> for which kinds <c>DurableWorkflowRuntime</c> currently executes.</param>
/// <param name="BranchTargets">For a <see cref="WorkflowStepKind.Branch"/> step, the declared candidate next-step identifiers.</param>
/// <param name="MaxIterations">For a <see cref="WorkflowStepKind.Loop"/> step, the maximum number of loop-body executions.</param>
/// <param name="TypedInputs">Typed, schema-referencing input bindings. Declarative only until a compiler/runtime validates and resolves them.</param>
/// <param name="TypedOutputs">Typed, schema-referencing output slots this step produces. Declarative only until a compiler/runtime validates and resolves them.</param>
/// <param name="ToolAllowList">Tool identifiers this step is permitted to invoke. Enforcement is a policy-engine concern, not this record's.</param>
/// <param name="MaxAutonomy">The maximum autonomy this step may exercise without a human in the loop.</param>
/// <param name="RequiredEvidenceIds">Identifiers of evidence records this step requires as a precondition.</param>
/// <param name="CompensationStepId">Identifier of a step in the same workflow that reverses this step's effect on saga rollback.</param>
/// <param name="OutputSchemaRef">Identifier of a schema this step's overall output must satisfy.</param>
/// <param name="TelemetryTags">Free-form key/value tags attached to this step's emitted telemetry.</param>
/// <param name="Effect">This step's declared reversibility/blast-radius classification.</param>
/// <param name="Budget">This step's declared token/cost/time budget, independent of the workflow's overall budget.</param>
/// <param name="EffectJustification">
/// Required by a compiler when <paramref name="Effect"/> is <see cref="EffectClass.Mutating"/> or
/// <see cref="EffectClass.Irreversible"/> and <paramref name="CompensationStepId"/> is not declared —
/// an explicit human-authored reason compensation is not provided for this step's effect.
/// </param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowStepDefinition(
    string StepId,
    string StepType,
    IReadOnlyList<string> DependsOn,
    IReadOnlyDictionary<string, string>? Inputs = null,
    TimeSpan? Timeout = null,
    WorkflowStepKind Kind = WorkflowStepKind.Sequential,
    IReadOnlyList<string>? BranchTargets = null,
    int? MaxIterations = null,
    IReadOnlyList<WorkflowStepInputBinding>? TypedInputs = null,
    IReadOnlyList<WorkflowStepOutputBinding>? TypedOutputs = null,
    IReadOnlyList<string>? ToolAllowList = null,
    AutonomyMode? MaxAutonomy = null,
    IReadOnlyList<string>? RequiredEvidenceIds = null,
    string? CompensationStepId = null,
    string? OutputSchemaRef = null,
    IReadOnlyDictionary<string, string>? TelemetryTags = null,
    EffectClass? Effect = null,
    WorkflowBudget? Budget = null,
    string? EffectJustification = null);

/// <summary>One metric an objective declares, with the direction that counts as improvement.</summary>
/// <param name="MetricId">Identifier of the metric, resolved against recorded <see cref="MetricObservation"/>s.</param>
/// <param name="Direction">Either <c>"maximize"</c> or <c>"minimize"</c>; interpretation belongs to the evaluator, not this record.</param>
/// <param name="Threshold">Optional pass/fail threshold for this metric; interpretation (vs. <paramref name="Direction"/>) belongs to the evaluator.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowObjectiveMetric(string MetricId, string Direction, double? Threshold = null);

/// <summary>The measurable goal a workflow is optimized against.</summary>
/// <param name="Metrics">The metrics that define success for this workflow.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowObjective(IReadOnlyList<WorkflowObjectiveMetric> Metrics);

/// <summary>Declared risk/policy constraints binding a workflow's autonomy.</summary>
/// <param name="MaxEffectClass">The most severe <see cref="EffectClass"/> any step in this workflow may declare.</param>
/// <param name="RequireApprovalFor">Effect classes that require an authenticated approval regardless of a step's declared <see cref="AutonomyMode"/>.</param>
/// <param name="Budget">The workflow's overall token/cost/time budget.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowPolicyBinding(
    EffectClass MaxEffectClass,
    IReadOnlyList<EffectClass>? RequireApprovalFor = null,
    WorkflowBudget? Budget = null);

/// <summary>One item of context (evidence or knowledge) a workflow declares it needs before it can run.</summary>
/// <param name="Kind">The category of context requested (e.g. <c>"evidence"</c>, <c>"knowledge"</c>).</param>
/// <param name="Identifier">The specific item identifier within <paramref name="Kind"/>.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowContextRequest(string Kind, string Identifier);

/// <summary>A workflow's declared plan for evaluating its own outcomes against its objective.</summary>
/// <param name="MetricIds">Identifiers of the metrics this plan evaluates.</param>
/// <param name="Enabled">Whether evaluation runs for this workflow.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowEvaluationPlan(IReadOnlyList<string> MetricIds, bool Enabled = true);

/// <summary>A workflow's declared policy for generating, evaluating, and promoting improved versions of itself.</summary>
/// <param name="Enabled">Whether self-optimization is permitted for this workflow.</param>
/// <param name="Promotion">The promotion strategy identifier (e.g. <c>"canary"</c>); interpretation belongs to the synthesis/promotion pipeline.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowOptimizationPolicy(bool Enabled, string? Promotion = null);

/// <summary>Tracks the promotion lifecycle of one version of a workflow definition.</summary>
/// <param name="WorkflowId">The workflow this version belongs to.</param>
/// <param name="SemanticVersion">The version's own semantic version string.</param>
/// <param name="State">The version's current point in the promotion lifecycle.</param>
/// <param name="CreatedAt">When this version was created.</param>
/// <param name="ArtifactHash">Content hash of the compiled artifact this version resolves to, once compiled.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowVersion(
    string WorkflowId,
    string SemanticVersion,
    WorkflowPromotionState State,
    DateTimeOffset CreatedAt,
    string? ArtifactHash = null);

/// <summary>
/// A compiled, content-addressed workflow artifact. Produced by a compiler (e.g. the YAML compiler) and
/// consumed by the runtime — this type lives in Contracts specifically so no particular front-end compiler
/// package needs to be referenced by the runtime that executes its output.
/// </summary>
/// <param name="ArtifactHash">Content hash of the compiled definition.</param>
/// <param name="SchemaHash">Content hash of the schema version the source was validated against.</param>
/// <param name="PolicyHash">Content hash of the policy bindings in effect at compile time.</param>
/// <param name="Definition">The compiled, statically-validated workflow definition.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowArtifact(
    string ArtifactHash,
    string SchemaHash,
    string PolicyHash,
    WorkflowDefinition Definition);

/// <summary>Request to execute one named workflow version.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowExecutionRequest(
    string ExecutionId,
    WorkflowDefinition Workflow,
    string InitiatorId,
    DateTimeOffset RequestedAt,
    IReadOnlyDictionary<string, string>? Inputs = null,
    string? CorrelationId = null);

/// <summary>Lifecycle state reported by a workflow runtime.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public enum WorkflowExecutionStatus
{
    /// <summary>Execution is accepted but has not started.</summary>
    Queued,
    /// <summary>Execution is actively progressing through workflow steps.</summary>
    Running,
    /// <summary>Execution is paused pending an explicit confirmation response.</summary>
    AwaitingConfirmation,
    /// <summary>Execution completed successfully.</summary>
    Succeeded,
    /// <summary>Execution terminated with a failure.</summary>
    Failed,
    /// <summary>Execution was cancelled before terminal success or failure.</summary>
    Cancelled,
}

/// <summary>Runtime-neutral snapshot of a workflow execution.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record WorkflowExecutionSnapshot(
    string ExecutionId,
    WorkflowExecutionStatus Status,
    string? CurrentStepId,
    DateTimeOffset UpdatedAt,
    string? FailureReason = null,
    string? CheckpointReference = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
