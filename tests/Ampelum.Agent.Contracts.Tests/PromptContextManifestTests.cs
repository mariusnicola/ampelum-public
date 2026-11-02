using SignalGrid.Ampelum.Agent.Contracts;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Contracts.Tests;

public sealed class PromptContextManifestTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BuildContextManifest_EvidenceWithinScopeAndPermission_Included()
    {
        var evidence = new EvidenceRecord("e1", "crawler", Now.AddDays(-1), "hash1", Scope: "workspace-a", Permissions: ["read:market"]);

        var manifest = PromptCompiler.BuildContextManifest(
            [evidence], [], ["workspace-a"], ["read:market"], [], [], null, null, Now);

        manifest.IncludedEvidenceIds.ShouldBe(["e1"]);
        manifest.Excluded.ShouldBeEmpty();
    }

    [Fact]
    public void BuildContextManifest_ExpiredEvidence_ExcludedWithReason()
    {
        var evidence = new EvidenceRecord("e1", "crawler", Now.AddDays(-2), "hash1", ValidUntil: Now.AddDays(-1));

        var manifest = PromptCompiler.BuildContextManifest(
            [evidence], [], [], [], [], [], null, null, Now);

        manifest.IncludedEvidenceIds.ShouldBeEmpty();
        manifest.Excluded.Single().ItemId.ShouldBe("e1");
        manifest.Excluded.Single().Reason.ShouldContain("expired");
    }

    [Fact]
    public void BuildContextManifest_EvidenceOutOfCallerScope_ExcludedWithReason()
    {
        var evidence = new EvidenceRecord("e1", "crawler", Now, "hash1", Scope: "workspace-b");

        var manifest = PromptCompiler.BuildContextManifest(
            [evidence], [], ["workspace-a"], [], [], [], null, null, Now);

        manifest.IncludedEvidenceIds.ShouldBeEmpty();
        manifest.Excluded.Single().Reason.ShouldContain("scope");
    }

    [Fact]
    public void BuildContextManifest_EvidenceMissingRequiredPermission_ExcludedWithReason()
    {
        var evidence = new EvidenceRecord("e1", "crawler", Now, "hash1", Permissions: ["read:sensitive"]);

        var manifest = PromptCompiler.BuildContextManifest(
            [evidence], [], [], ["read:public"], [], [], null, null, Now);

        manifest.IncludedEvidenceIds.ShouldBeEmpty();
        manifest.Excluded.Single().Reason.ShouldContain("permission");
    }

    [Fact]
    public void BuildContextManifest_NoBudgetDeclared_RemainingBudgetIsNull()
    {
        var manifest = PromptCompiler.BuildContextManifest([], [], [], [], [], [], null, null, Now);

        manifest.RemainingBudget.ShouldBeNull();
    }

    [Fact]
    public void BuildContextManifest_BudgetWithUsage_ReturnsRemainder()
    {
        var budget = new WorkflowBudget(MaxCost: 100m, MaxTokens: 1000);
        var usage = new WorkflowBudgetUsage(Cost: 30m, Tokens: 400);

        var manifest = PromptCompiler.BuildContextManifest([], [], [], [], [], [], budget, usage, Now);

        manifest.RemainingBudget!.Cost.ShouldBe(70m);
        manifest.RemainingBudget.Tokens.ShouldBe(600);
    }

    [Fact]
    public void BuildContextManifest_PartialBudgetDeclaresOnlyTokens_UndeclaredDimensionsAreNullNotZero()
    {
        // Regression test: an undeclared budget dimension must read as null ("no limit was ever declared"),
        // never as a literal 0 -- collapsing "no limit" and "limit exhausted" onto the same value would make
        // the manifest lie about which constraints are actually in effect.
        var budget = new WorkflowBudget(MaxTokens: 1000);

        var manifest = PromptCompiler.BuildContextManifest([], [], [], [], [], [], budget, null, Now);

        manifest.RemainingBudget!.Tokens.ShouldBe(1000);
        manifest.RemainingBudget.Cost.ShouldBeNull();
        manifest.RemainingBudget.Duration.ShouldBeNull();
    }

    [Fact]
    public void BuildContextManifest_UsageExceedsBudget_RemainderClampsToZeroRatherThanGoingNegative()
    {
        var budget = new WorkflowBudget(MaxCost: 10m);
        var usage = new WorkflowBudgetUsage(Cost: 15m);

        var manifest = PromptCompiler.BuildContextManifest([], [], [], [], [], [], budget, usage, Now);

        manifest.RemainingBudget!.Cost.ShouldBe(0m);
    }

    [Fact]
    public void BuildContextManifest_KnowledgePolicyAndToolIds_PassThroughAsIncluded()
    {
        var manifest = PromptCompiler.BuildContextManifest(
            [], ["playbook-1"], [], [], ["policy-1"], ["read_file"], null, null, Now);

        manifest.IncludedKnowledgeIds.ShouldBe(["playbook-1"]);
        manifest.IncludedPolicyIds.ShouldBe(["policy-1"]);
        manifest.AvailableToolIds.ShouldBe(["read_file"]);
    }
}
