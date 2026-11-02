using SignalGrid.Ampelum.Agent.Contracts;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Contracts.Tests;

public sealed class PromptCompilerTests
{
    [Fact]
    public void Compile_Same_Logical_Request_Is_Deterministic_Despite_Input_Order()
    {
        var first = PromptCompiler.Compile(Request(new[]
        {
            new PromptInputValue("account", PromptInputKind.Text, "Northwind"),
            new PromptInputValue("limit", PromptInputKind.Integer, "4"),
        }));
        var second = PromptCompiler.Compile(Request(new[]
        {
            new PromptInputValue("limit", PromptInputKind.Integer, "4"),
            new PromptInputValue("account", PromptInputKind.Text, "Northwind"),
        }));

        first.ArtifactHash.ShouldBe(second.ArtifactHash);
        first.InputSchemaHash.ShouldBe(second.InputSchemaHash);
        first.ToolPolicyHash.ShouldBe(second.ToolPolicyHash);
        first.RenderedPrompt.ShouldBe("Summarize Northwind with at most 4 findings.");
    }

    [Fact]
    public void Compile_Template_Schema_Or_ToolPolicy_Change_Changes_Artifact_Hash()
    {
        var baseline = PromptCompiler.Compile(Request());
        var templateChanged = PromptCompiler.Compile(Request(template: "Classify {{account}} with limit {{limit}}."));
        var schemaChanged = PromptCompiler.Compile(Request(schema: new[]
        {
            new PromptInputSchema("account", PromptInputKind.Text, Description: "Different schema contract"),
            new PromptInputSchema("limit", PromptInputKind.Integer),
        }));
        var policyChanged = PromptCompiler.Compile(Request(policy: new PromptToolPolicyBinding("research", "v2", new[] { "search" })));

        baseline.ArtifactHash.ShouldNotBe(templateChanged.ArtifactHash);
        baseline.ArtifactHash.ShouldNotBe(schemaChanged.ArtifactHash);
        baseline.ArtifactHash.ShouldNotBe(policyChanged.ArtifactHash);
    }

    [Fact]
    public void Compile_Freezes_Output_And_Preserves_Template_Version()
    {
        var inputList = new List<PromptInputValue>
        {
            new("account", PromptInputKind.Text, "Northwind"),
            new("limit", PromptInputKind.Integer, "4"),
        };
        var artifact = PromptCompiler.Compile(Request(inputList));
        inputList[0] = new PromptInputValue("account", PromptInputKind.Text, "Changed later");
        inputList.Add(new PromptInputValue("extra", PromptInputKind.Text, "ignored"));

        artifact.TemplateVersion.ShouldBe("v1");
        artifact.InputNames.ShouldBe(new[] { "account", "limit" });
        artifact.RenderedPrompt.ShouldContain("Northwind");
        artifact.RenderedPrompt.ShouldNotContain("Changed later");
    }

    [Fact]
    public void Evaluation_Audit_And_Replay_Correlate_Without_Secret_Input_Values()
    {
        const string secret = "do-not-audit-this-secret";
        var artifact = PromptCompiler.Compile(Request(new[]
        {
            new PromptInputValue("account", PromptInputKind.Text, secret),
            new PromptInputValue("limit", PromptInputKind.Integer, "4"),
        }, schema: new[]
        {
            new PromptInputSchema("account", PromptInputKind.Text, Secret: true),
            new PromptInputSchema("limit", PromptInputKind.Integer),
        }));
        var evaluation = PromptCompiler.CreateEvaluationRequest(artifact, "eval-1", "exec-1", "rubric-v1", DateTimeOffset.Parse("2026-07-27T10:00:00Z"), "replay-1");
        var audit = PromptCompiler.CreateAuditEvent(artifact, evaluation, "replay-1", "audit-1", "system", DateTimeOffset.Parse("2026-07-27T10:00:01Z"));
        var correlation = PromptCompiler.CreateCorrelation(artifact, evaluation, "replay-1");

        evaluation.Evidence["prompt.artifact.hash"].ShouldBe(artifact.ArtifactHash);
        audit.CorrelationId.ShouldBe(correlation.ReplayCorrelationId);
        audit.Metadata!["prompt.evaluation.id"].ShouldBe(correlation.EvaluationId);
        audit.Metadata.Values.ShouldNotContain(secret);
        audit.Metadata.Keys.ShouldNotContain("account");
        string.Join('|', audit.Metadata.Select(x => x.Key + "=" + x.Value)).ShouldNotContain(secret);
    }

    static PromptCompilationRequest Request(
        IReadOnlyList<PromptInputValue>? inputs = null,
        string? template = null,
        IReadOnlyList<PromptInputSchema>? schema = null,
        PromptToolPolicyBinding? policy = null)
        => new(
            new PromptTemplateDefinition(
                "account-summary",
                "v1",
                template ?? "Summarize {{account}} with at most {{limit}} findings.",
                schema ?? new[]
                {
                    new PromptInputSchema("account", PromptInputKind.Text),
                    new PromptInputSchema("limit", PromptInputKind.Integer),
                },
                policy ?? new PromptToolPolicyBinding("research", "v1", new[] { "search" })),
            inputs ?? new[]
            {
                new PromptInputValue("account", PromptInputKind.Text, "Northwind"),
                new PromptInputValue("limit", PromptInputKind.Integer, "4"),
            });
}
