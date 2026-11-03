using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Usage;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>What the run concluded about one issue.</summary>
public sealed record IssueResult(
    int Issue,
    string Title,
    string Severity,
    string Cause,
    string NextStep,
    string Skill,
    string SkillRoute,
    string BriefHash,
    IReadOnlyList<string> Violations,
    IReadOnlyList<string> EvidenceUsed,
    IReadOnlyList<string> EvidenceWithheld);

/// <summary>An issue the run did not get to, and why.</summary>
public sealed record DeferredIssue(int Issue, string Reason);

/// <summary>Collects what the audits produced. Audits run in parallel, so the collections are concurrent.</summary>
public sealed class AuditResults
{
    readonly ConcurrentDictionary<int, IssueResult> _results = new();
    readonly ConcurrentDictionary<int, DeferredIssue> _deferred = new();
    readonly ConcurrentQueue<AgentAuditEvent> _events = new();

    public IReadOnlyList<IssueResult> Results => [.. _results.Values.OrderBy(r => r.Issue)];

    public IReadOnlyList<DeferredIssue> Deferred => [.. _deferred.Values.OrderBy(d => d.Issue)];

    public IReadOnlyList<AgentAuditEvent> PromptAudit => [.. _events];

    internal void Add(IssueResult result, AgentAuditEvent audit)
    {
        _results[result.Issue] = result;
        _deferred.TryRemove(result.Issue, out _);
        _events.Enqueue(audit);
    }

    internal void Defer(int issue, string reason) => _deferred[issue] = new DeferredIssue(issue, reason);
}

/// <summary>Assesses one issue: gathers code facts, picks a skill, compiles the brief and asks the model.</summary>
public sealed class IssueAuditor(
    string root,
    string executionId,
    TriagePolicy policy,
    BlastRadiusScope readable,
    CodeFacts facts,
    TriageSkills skills,
    Func<string, IChatClient> clientFor,
    RunJournal journal,
    ModelBudget budget,
    AuditResults results)
{
    const string AuditTool = TriageTools.Audit;
    const string CodeScope = "code";

    static readonly PromptTemplateDefinition Template = new(
        TemplateId: "triage-brief",
        Version: "1",
        Template: Embedded.ReadText("brief-template.txt"),
        InputSchema:
        [
            new PromptInputSchema("issue", PromptInputKind.Integer),
            new PromptInputSchema("title", PromptInputKind.Text),
            new PromptInputSchema("skill", PromptInputKind.Text),
            new PromptInputSchema("facts", PromptInputKind.Text),
        ],
        ToolPolicy: new PromptToolPolicyBinding("triage-tools", "1", [AuditTool]));

    /// <summary>
    /// Assesses the issue and records the result. When the budget has no room left the issue is recorded as deferred
    /// and the refusal is rethrown to the caller.
    /// </summary>
    public async Task<IssueResult> AuditAsync(TriageIssue issue, CancellationToken ct = default)
    {
        try
        {
            return await AssessAsync(issue, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (BudgetExhaustedException.Find(ex) is { } budgetRefusal)
        {
            results.Defer(issue.Number, budgetRefusal.Message);
            throw;
        }
    }

    async Task<IssueResult> AssessAsync(TriageIssue issue, CancellationToken ct)
    {
        var found = await facts.GatherAsync(issue, ct).ConfigureAwait(false);
        foreach (var file in found.Files)
        {
            var path = Path.GetFullPath(Path.Combine(root, file));
            if (!readable.ContainsPath(path))
                throw new InvalidOperationException($"'{file}' is outside the readable scope.");
            await journal.RecordFileReadAsync(file, await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }

        var choice = await skills.ChooseAsync(issue, clientFor("classify"), ct).ConfigureAwait(false);
        var brief = PromptCompiler.Compile(new PromptCompilationRequest(Template,
        [
            new PromptInputValue("issue", PromptInputKind.Integer, issue.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new PromptInputValue("title", PromptInputKind.Text, issue.Title),
            new PromptInputValue("skill", PromptInputKind.Text, choice.Skill?.SystemPrompt ?? "none"),
            new PromptInputValue("facts", PromptInputKind.Text, FormatFacts(found)),
        ]));

        var manifest = BuildManifest();
        var tracker = new DefaultUsageTracker();
        var predict = new Predict<IssueAssessment>(new AmpelumContext(clientFor("analyse"), new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance) { Usage = tracker });
        var answer = await predict.ForwardAsync(new IssueAssessment { Brief = brief.RenderedPrompt }, ct: ct).ConfigureAwait(false);
        await journal.RecordUsageAsync(answer.Usage(), ct).ConfigureAwait(false);

        var evaluation = PromptCompiler.CreateEvaluationRequest(brief, $"{executionId}:{issue.Number}", executionId, "triage-auditor", DateTimeOffset.UtcNow, executionId);
        var audit = PromptCompiler.CreateAuditEvent(brief, evaluation, executionId, Guid.NewGuid().ToString("N"), "triage-auditor", DateTimeOffset.UtcNow);

        var result = new IssueResult(
            issue.Number, issue.Title, answer.Value.Severity, answer.Value.Cause, answer.Value.NextStep,
            choice.Skill?.Name ?? "none", choice.Route.ToString(), brief.ArtifactHash,
            found.Violations, manifest.IncludedEvidenceIds, [.. manifest.Excluded.Select(e => e.ItemId)]);
        results.Add(result, audit);
        return result;
    }

    static string FormatFacts(IssueFacts found) =>
        string.Join('\n', new[] { "Types:" }.Concat(found.Symbols)
            .Concat(["Imports:"]).Concat(found.Imports)
            .Concat(["Convention findings:"]).Concat(found.Violations));

    // Every project file is a piece of evidence. A caller may only use files under a folder it holds a read permission for.
    PromptContextManifest BuildManifest()
    {
        var now = DateTimeOffset.UtcNow;
        var evidence = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".ampelum" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order()
            .Select(rel => new EvidenceRecord(
                rel, "project", now, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(root, rel))))),
                Scope: CodeScope, Permissions: [$"read:{Folder(rel)}"]))
            .ToArray();

        return PromptCompiler.BuildContextManifest(
            evidence, [], [CodeScope], [.. policy.ReadablePaths.Select(p => $"read:{p}")],
            ["triage-tools"], [AuditTool],
            new WorkflowBudget(MaxTokens: (int)Math.Min(policy.MaxTokens, int.MaxValue)),
            new WorkflowBudgetUsage(Tokens: (int)Math.Min(budget.Tokens, int.MaxValue)), now);
    }

    static string Folder(string relative) => relative.Contains('/', StringComparison.Ordinal) ? relative[..relative.IndexOf('/', StringComparison.Ordinal)] : "root";
}
