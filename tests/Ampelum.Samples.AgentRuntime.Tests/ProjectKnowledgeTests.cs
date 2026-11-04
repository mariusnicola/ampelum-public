using Ampelum.Samples.AgentRuntime;
using Shouldly;
using SignalGrid.Ampelum.Agent;
using Xunit;

namespace Ampelum.Samples.AgentRuntime.Tests;

public sealed class ProjectKnowledgeTests : IDisposable
{
    readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void Backlog_IsReadFromTheProjectFolder()
    {
        DemoProject.ReadBacklog(_workspace.Root).Select(i => (i.Number, string.Join("+", i.Labels))).ShouldBe(
            [(101, "bug+orders"), (102, "bug+pricing"), (103, "hygiene+llm")]);
    }

    [Fact]
    public void Workspace_IsStoredWithItsMetadata_AndGetsItsScopesBackAfterAReload()
    {
        var file = Path.Combine(_workspace.State, "portfolio.json");
        var skills = new TriageSkills(_workspace.Root);
        var first = TriageWorkspace.Open(new JsonFileProjectRegistry(file), _workspace.Root, skills);

        var reloaded = TriageWorkspace.Open(new JsonFileProjectRegistry(file), _workspace.Root, skills);

        reloaded.Id.ShouldBe(first.Id);
        reloaded.Label.ShouldBe("Shop checkout");
        reloaded.RootPath.ShouldBe(_workspace.Root);
        reloaded.EscalationPolicy.Mode.ShouldBe(EscalationMode.Queue);
        reloaded.MaxBlastRadius.ContainsPath(Path.Combine(_workspace.Root, "src", "Pricing.cs")).ShouldBeTrue();
        reloaded.AllowedToolIds.ShouldBe([TriageTools.Audit, TriageTools.PostReport], ignoreOrder: true);
        new JsonFileProjectRegistry(file).GetAll().ShouldHaveSingleItem();
    }

    [Fact]
    public void Workspace_TakesItsBlastRadiusAndToolsFromTheRegistryFile_NotFromTheDomainProfile()
    {
        var file = Path.Combine(_workspace.State, "portfolio.json");
        var skills = new TriageSkills(_workspace.Root);
        TriageWorkspace.Open(new JsonFileProjectRegistry(file), _workspace.Root, skills);

        // Narrow what the file stores; a profile rebuilt on load would ignore this.
        var stored = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        var workspace = stored["workspaces"]![0]!;
        workspace["allowedToolIds"] = new System.Text.Json.Nodes.JsonArray(TriageTools.Audit);
        workspace["blastRadius"]!["tools"] = new System.Text.Json.Nodes.JsonArray(TriageTools.Audit);
        File.WriteAllText(file, stored.ToJsonString());

        var reloaded = TriageWorkspace.Open(new JsonFileProjectRegistry(file), _workspace.Root, skills);

        reloaded.AllowedToolIds.ShouldBe([TriageTools.Audit]);
        reloaded.MaxBlastRadius.ContainsTool(TriageTools.PostReport).ShouldBeFalse();
        reloaded.MaxBlastRadius.ContainsTool(TriageTools.Audit).ShouldBeTrue();
    }

    [Fact]
    public async Task Registries_RefuseTheSameWorkspaceTwice_AndForgetOneThatIsDeregistered()
    {
        var registry = new InMemoryProjectRegistry();
        var workspace = TriageWorkspace.Open(registry, _workspace.Root, new TriageSkills(_workspace.Root));

        registry.IsEmpty.ShouldBeFalse();
        registry.Get(TriageWorkspace.Id).ShouldNotBeNull();
        Should.Throw<DuplicateWorkspaceException>(() => registry.Register(workspace));

        await registry.DeregisterAsync(TriageWorkspace.Id, TestContext.Current.CancellationToken);

        registry.IsEmpty.ShouldBeTrue();
        registry.Get(TriageWorkspace.Id).ShouldBeNull();
    }

    [Fact]
    public async Task Facts_NameTheTypesOfAnIssue_TheirImports_AndTheProjectsOwnConventionFindings()
    {
        using var facts = Facts();

        var order = await facts.GatherAsync(Issue(101, "Order total is wrong for an empty cart"), TestContext.Current.CancellationToken);
        var notifier = await facts.GatherAsync(Issue(103, "Notifier calls the model with a raw call"), TestContext.Current.CancellationToken);

        order.Symbols.ShouldBe(["Class OrderService in src/OrderService.cs"]);
        order.Violations.ShouldContain(v => v.StartsWith("src/OrderService.cs: max-file-lines (warning)", StringComparison.Ordinal));
        order.Violations.ShouldContain(v => v.StartsWith("src/OrderService.cs: no-dynamic (warning)", StringComparison.Ordinal));
        notifier.Symbols.ShouldBe(["Class Notifier in src/Notifier.cs"]);
        notifier.Imports.ShouldBe(["src/Notifier.cs imports Microsoft.Extensions.AI"]);
        notifier.Violations.ShouldContain(v => v.StartsWith("src/Notifier.cs: no-bare-llm-prompts (error)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Facts_FindNothingInAFileTheScopeDoesNotCover()
    {
        var nothing = new BlastRadiusScope { AllowedResourcePaths = new HashSet<string> { Path.Combine(_workspace.Root, "docs") + Path.DirectorySeparatorChar } };
        using var facts = new CodeFacts(_workspace.Root, [Path.Combine(_workspace.Root, "src")], nothing);

        var found = await facts.GatherAsync(Issue(103, "Notifier calls the model with a raw call"), TestContext.Current.CancellationToken);

        found.Symbols.ShouldBeEmpty();
        found.Violations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Facts_ForTheCleanFile_ReportNoConventionFinding()
    {
        using var facts = Facts();

        var pricing = await facts.GatherAsync(Issue(102, "Pricing.ApplyDiscount never rounds"), TestContext.Current.CancellationToken);

        pricing.Symbols.ShouldBe(["Class Pricing in src/Pricing.cs"]);
        pricing.Violations.ShouldNotContain(v => v.Contains("Pricing.cs", StringComparison.Ordinal));
    }

    CodeFacts Facts() => new(
        _workspace.Root,
        [Path.Combine(_workspace.Root, "src")],
        new BlastRadiusScope { AllowedResourcePaths = new HashSet<string> { Path.Combine(_workspace.Root, "src") + Path.DirectorySeparatorChar } });

    static TriageIssue Issue(int number, string title) => new(number, title, []);
}
