using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;
using SignalGrid.Ampelum.Agent.Yaml;

namespace Ampelum.Samples.ExpenseApproval;

/// <summary>
/// Expense approval as a durable YAML workflow: extract the expense with a model, branch on the amount,
/// pay or flag for review through the governed tool path, join. Then the workflow waits for the receipt
/// (WaitForSignal), checks the receipt against the expense (Evaluate), and announces the settlement
/// (EmitEvent). When the receipt does not match, the payment is refunded (Compensate). A run can be stopped
/// after its first step and resumed from the database by a fresh runtime.
/// </summary>
public sealed class ExpenseApprovalPipeline
{
    /// <summary>Tool name of the auto-pay step; must match the YAML.</summary>
    public const string PayTool = "pay_expense";

    /// <summary>Tool name of the flag-for-review step; must match the YAML.</summary>
    public const string ReviewTool = "flag_for_review";

    /// <summary>Name of the signal the receipt step waits for; must match the YAML.</summary>
    public const string ReceiptSignal = "receipt";

    /// <summary>Metric the evaluate step observes; must match the YAML objective.</summary>
    public const string ReceiptMetric = "receipt_matches";

    const string WorkflowResource = "expense-approval.yml";
    const string TextInput = "text";
    const string LimitInput = "limit";
    const string KeyArgument = "idempotency_key";

    readonly IChatClient _chat;
    readonly IExpenseLedger _ledger;
    readonly WorkflowDefinition _definition;
    readonly Func<ExpenseStore> _openStore;

    /// <summary>Creates the pipeline on a SQLite file.</summary>
    /// <param name="chat">Chat client used for extraction.</param>
    /// <param name="ledger">Receives the pay and flag-for-review side effects.</param>
    /// <param name="definition">Compiled workflow, normally from <see cref="LoadDefinition"/>.</param>
    /// <param name="databasePath">SQLite file that holds checkpoints and audit history.</param>
    public ExpenseApprovalPipeline(IChatClient chat, IExpenseLedger ledger, WorkflowDefinition definition, string databasePath)
        : this(chat, ledger, definition, StoreFactory(databasePath))
    {
    }

    /// <summary>Creates the pipeline on any store.</summary>
    /// <param name="chat">Chat client used for extraction.</param>
    /// <param name="ledger">Receives the pay and flag-for-review side effects.</param>
    /// <param name="definition">Compiled workflow, normally from <see cref="LoadDefinition"/>.</param>
    /// <param name="openStore">Called once per runtime; must return a new persistence object each time.</param>
    public ExpenseApprovalPipeline(IChatClient chat, IExpenseLedger ledger, WorkflowDefinition definition, Func<ExpenseStore> openStore)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _openStore = openStore ?? throw new ArgumentNullException(nameof(openStore));
    }

    static Func<ExpenseStore> StoreFactory(string databasePath)
    {
        ArgumentNullException.ThrowIfNull(databasePath);
        return () => ExpenseStore.Sqlite(databasePath);
    }

    /// <summary>
    /// Compiles the embedded workflow YAML. The YAML itself declares the metric's threshold and the evaluation plan.
    /// </summary>
    public static WorkflowDefinition LoadDefinition()
    {
        using var stream = typeof(ExpenseApprovalPipeline).Assembly.GetManifestResourceStream(WorkflowResource)
            ?? throw new InvalidOperationException($"Embedded workflow '{WorkflowResource}' is missing.");
        using var reader = new StreamReader(stream);
        var result = WorkflowYamlCompiler.Compile(reader.ReadToEnd());
        if (!result.Success || result.Artifact is null)
            throw new InvalidOperationException("Workflow YAML did not compile: " + string.Join("; ", result.Errors));

        return result.Artifact.Definition;
    }

    /// <summary>
    /// Runs the workflow, stops it after the first step, then resumes it on a new runtime and persistence.
    /// <paramref name="receiptAmount"/> is the amount on the receipt; null means the receipt shows the extracted amount.
    /// </summary>
    public async Task<ExpenseApprovalResult> RunWithRestartAsync(string text, CancellationToken ct = default, string? executionId = null, string? receiptAmount = null)
    {
        executionId ??= Guid.NewGuid().ToString("N");
        await using var stopped = await StartAndStopAsync(executionId, text, ct).ConfigureAwait(false);
        return await ResumeAsync(executionId, ct, receiptAmount).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts an execution and leaves it interrupted once its first step has completed and the next one is
    /// claimed but not run, as after a process crash. Dispose the returned handle after resuming.
    /// </summary>
    public async Task<InterruptedExpenseRun> StartAndStopAsync(string executionId, string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var store = _openStore();
        var runtime = CreateRuntime(store);
        var request = new WorkflowExecutionRequest(
            executionId,
            _definition,
            nameof(ExpenseApprovalPipeline),
            DateTimeOffset.UtcNow,
            new Dictionary<string, string> { [TextInput] = text });

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandon = new CancellationTokenSource();
        var firstStepId = _definition.Steps[0].StepId;
        var handler = CreateHandler(store, async step =>
        {
            if (step.StepId == firstStepId) return;
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, abandon.Token).ConfigureAwait(false);
        });

        var run = runtime.RunAsync(request, handler, ct);
        var first = await Task.WhenAny(reached.Task, run).ConfigureAwait(false);
        if (first == run)
        {
            abandon.Dispose();
            var snapshot = await run.ConfigureAwait(false);
            throw new InvalidOperationException($"Execution '{executionId}' ended as {snapshot.Status} before it could be stopped: {snapshot.FailureReason}");
        }
        return new InterruptedExpenseRun(run, abandon);
    }

    /// <summary>
    /// Resumes a stored execution on a new persistence and runtime and returns its outcome. When the execution stops
    /// at the receipt step, the receipt is delivered here, on another new runtime, and the execution is resumed again.
    /// </summary>
    /// <param name="executionId">The stored execution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="receiptAmount">Amount on the receipt; null means the receipt shows the extracted amount.</param>
    public async Task<ExpenseApprovalResult> ResumeAsync(string executionId, CancellationToken ct = default, string? receiptAmount = null)
    {
        var waiting = await ResumeUntilReceiptAsync(executionId, ct).ConfigureAwait(false);
        if (waiting.Status != WorkflowExecutionStatus.AwaitingConfirmation)
            return waiting;
        await DeliverReceiptAsync(executionId, receiptAmount ?? waiting.Expense!.Amount.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        return await ResumeUntilReceiptAsync(executionId, ct).ConfigureAwait(false);
    }

    /// <summary>Resumes a stored execution on a new persistence and runtime. It returns <see cref="WorkflowExecutionStatus.AwaitingConfirmation"/> while the receipt has not arrived.</summary>
    public async Task<ExpenseApprovalResult> ResumeUntilReceiptAsync(string executionId, CancellationToken ct = default)
    {
        var store = _openStore();
        var persisted = await store.LoadAsync(executionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No stored execution '{executionId}'.");
        var runtime = CreateRuntime(store);
        var snapshot = await runtime.ResumeAsync(persisted.Request, CreateHandler(store, null), ct).ConfigureAwait(false);
        return await BuildResultAsync(store, snapshot, ct).ConfigureAwait(false);
    }

    /// <summary>Delivers the receipt to a stored execution on a new persistence and runtime. The amount is the receipt's payload.</summary>
    public async Task DeliverReceiptAsync(string executionId, string amount, CancellationToken ct = default)
    {
        var store = _openStore();
        await CreateRuntime(store).SignalAsync(executionId, ReceiptSignal, amount, nameof(ExpenseApprovalPipeline), ct).ConfigureAwait(false);
    }

    static DurableWorkflowRuntime CreateRuntime(ExpenseStore store) =>
        new(store.Persistence, store.Persistence, evaluator: new ReceiptEvaluator());

    Func<DurableWorkflowNodeContext, CancellationToken, Task<DurableWorkflowNodeResult>> CreateHandler(
        ExpenseStore store,
        Func<WorkflowStepDefinition, Task>? beforeStep)
    {
        var dispatcher = CreateDispatcher(out var caller);
        var gate = new WorkflowPolicyGate();

        return async (context, ct) =>
        {
            if (beforeStep is not null)
                await beforeStep(context.Step).ConfigureAwait(false);
            switch (context.Step.Kind)
            {
                case WorkflowStepKind.Sequential:
                    return await ExtractAsync(context, ct).ConfigureAwait(false);
                case WorkflowStepKind.Branch:
                    return await RouteAsync(store, context, ct).ConfigureAwait(false);
                case WorkflowStepKind.Tool:
                    var expense = await LoadExpenseAsync(store, context.Request, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("No extracted expense is stored for this execution.");
                    var toolStep = new WorkflowToolStepHandler(dispatcher, caller, gate, node => ToolArguments(expense, node.IdempotencyKey));
                    return await toolStep.HandleAsync(context, ct).ConfigureAwait(false);
                case WorkflowStepKind.Join:
                    return DurableWorkflowNodeResult.Success();
                case WorkflowStepKind.Compensate:
                    // The runtime calls this only after the payment succeeded and a later step failed.
                    var paid = await LoadExpenseAsync(store, context.Request, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("No extracted expense is stored for this execution.");
                    await _ledger.RefundAsync(paid, context.IdempotencyKey, ct).ConfigureAwait(false);
                    return DurableWorkflowNodeResult.Success("refunded");
                default:
                    // WaitForSignal, Evaluate and EmitEvent never reach the handler: the runtime settles them itself.
                    return DurableWorkflowNodeResult.Failure($"Step '{context.Step.StepId}' has unsupported kind '{context.Step.Kind}'.");
            }
        };
    }

    async Task<DurableWorkflowNodeResult> ExtractAsync(DurableWorkflowNodeContext context, CancellationToken ct)
    {
        var text = context.Request.Inputs is { } inputs && inputs.TryGetValue(TextInput, out var value)
            ? value
            : throw new InvalidOperationException($"Execution input '{TextInput}' is missing.");
        var prediction = await Predict.Create<ExpenseExtraction>(_chat)
            .ForwardAsync(new ExpenseExtraction { Text = text }, ct: ct).ConfigureAwait(false);
        var expense = ValidatedExpense(prediction.Value);
        // The node detail is persisted with the checkpoint, so later steps and a resumed run read it back.
        return DurableWorkflowNodeResult.Success(JsonSerializer.Serialize(expense));
    }

    // An amount that is not a positive decimal is kept as a review reason, never as a payable amount.
    static Expense ValidatedExpense(ExpenseExtraction extraction)
    {
        var raw = extraction.Amount.Trim();
        if (!decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
            return new Expense(extraction.Vendor, 0m, extraction.Category, $"Amount '{raw}' is not a valid number.");
        if (amount <= 0)
            return new Expense(extraction.Vendor, amount, extraction.Category, $"Amount '{raw}' must be greater than zero.");
        return new Expense(extraction.Vendor, amount, extraction.Category);
    }

    static async Task<DurableWorkflowNodeResult> RouteAsync(ExpenseStore store, DurableWorkflowNodeContext context, CancellationToken ct)
    {
        var step = context.Step;
        if (step.Inputs is null || !step.Inputs.TryGetValue(LimitInput, out var rawLimit)
            || !decimal.TryParse(rawLimit, NumberStyles.Number, CultureInfo.InvariantCulture, out var limit))
            return DurableWorkflowNodeResult.Failure($"Branch step '{step.StepId}' needs a numeric '{LimitInput}' input.");
        var expense = await LoadExpenseAsync(store, context.Request, ct).ConfigureAwait(false);
        if (expense is null)
            return DurableWorkflowNodeResult.Failure("No extracted expense is stored for this execution.");

        var payable = expense.ReviewReason is null && expense.Amount > 0 && expense.Amount <= limit;
        var wantedTool = payable ? PayTool : ReviewTool;
        var target = context.Request.Workflow.Steps.FirstOrDefault(s =>
            step.BranchTargets!.Contains(s.StepId) && s.StepType == wantedTool);
        return target is null
            ? DurableWorkflowNodeResult.Failure($"Branch step '{step.StepId}' has no target for tool '{wantedTool}'.")
            : new DurableWorkflowNodeResult(true, SelectedNextStepId: target.StepId);
    }

    static async Task<Expense?> LoadExpenseAsync(ExpenseStore store, WorkflowExecutionRequest request, CancellationToken ct)
    {
        var extractStep = request.Workflow.Steps.First(s => s.Kind == WorkflowStepKind.Sequential);
        var snapshot = await store.LoadAsync(request.ExecutionId, ct).ConfigureAwait(false);
        var detail = snapshot?.Nodes[extractStep.StepId].Detail;
        return detail is null ? null : JsonSerializer.Deserialize<Expense>(detail);
    }

    // The dispatcher hands a registered handler only its arguments, so the node's idempotency key travels as one.
    internal static Dictionary<string, JsonElement> ToolArguments(Expense expense, string idempotencyKey) => new()
    {
        [KeyArgument] = JsonSerializer.SerializeToElement(idempotencyKey),
        ["reason"] = JsonSerializer.SerializeToElement(expense.ReviewReason),
        ["vendor"] = JsonSerializer.SerializeToElement(expense.Vendor),
        ["amount"] = JsonSerializer.SerializeToElement(expense.Amount),
        ["category"] = JsonSerializer.SerializeToElement(expense.Category),
    };

    static Expense ReadExpense(IReadOnlyDictionary<string, JsonElement> args) =>
        new(args["vendor"].GetString() ?? "", args["amount"].GetDecimal(), args["category"].GetString() ?? "", args["reason"].GetString());

    internal ToolDispatcher CreateDispatcher(out AgentIdentity identity)
    {
        var loader = new ExpenseToolLoader(
        [
            Manifest(PayTool, "Pays an expense that is within the approval limit."),
            Manifest(ReviewTool, "Flags an expense for human review."),
        ]);
        identity = new AgentIdentity(nameof(ExpenseApprovalPipeline), Guid.NewGuid().ToString("N"), ["expense"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var dispatcher = new ToolDispatcher(loader, new InMemoryTokenStore(), new PolicyEngine(), identity);
        dispatcher.RegisterHandler(PayTool, async (args, ct) =>
        {
            await _ledger.PayAsync(ReadExpense(args), args[KeyArgument].GetString()!, ct).ConfigureAwait(false);
            return new ToolResult(true, "paid", null, null);
        });
        dispatcher.RegisterHandler(ReviewTool, async (args, ct) =>
        {
            await _ledger.FlagForReviewAsync(ReadExpense(args), args[KeyArgument].GetString()!, ct).ConfigureAwait(false);
            return new ToolResult(true, "flagged", null, null);
        });
        return dispatcher;
    }

    static ToolManifest Manifest(string name, string description) => new()
    {
        Name = name,
        Description = description,
        DangerTier = DangerTier.Additive,
        Reversibility = Reversibility.DurableWithUndo,
    };

    static async Task<ExpenseApprovalResult> BuildResultAsync(ExpenseStore store, DurableWorkflowSnapshot snapshot, CancellationToken ct)
    {
        var request = snapshot.Request;
        var executed = request.Workflow.Steps
            .Where(s => s.Kind == WorkflowStepKind.Tool && snapshot.Nodes[s.StepId].Status == DurableWorkflowNodeStatus.Succeeded)
            .Select(s => s.StepType)
            .SingleOrDefault();
        var audit = await store.QueryAuditAsync(request.ExecutionId, ct).ConfigureAwait(false);
        var expense = await LoadExpenseAsync(store, request, ct).ConfigureAwait(false);
        return new ExpenseApprovalResult(request.ExecutionId, snapshot.Status, expense, executed, snapshot.FailureReason, audit);
    }
}

/// <summary>Handle to an execution stopped after its first step. Disposing it releases the abandoned first runtime.</summary>
public sealed class InterruptedExpenseRun : IAsyncDisposable
{
    readonly Task<DurableWorkflowSnapshot> _run;
    readonly CancellationTokenSource _abandon;

    internal InterruptedExpenseRun(Task<DurableWorkflowSnapshot> run, CancellationTokenSource abandon)
    {
        _run = run;
        _abandon = abandon;
    }

    /// <summary>Lets the abandoned runtime finish; it loses its checkpoint race against the resumed one and writes nothing.</summary>
    public async ValueTask DisposeAsync()
    {
        await _abandon.CancelAsync().ConfigureAwait(false);
        try
        {
            await _run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _abandon.Dispose();
    }
}
