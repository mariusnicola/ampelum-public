using Ampelum.Samples.AgentRuntime;
using Shouldly;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Samples.AgentRuntime.Tests;

public sealed class SkillsTests
{
    readonly TriageSkills _skills = new();

    [Fact]
    public async Task SkillCarryingEveryLabel_IsChosenWithoutAskingTheModel()
    {
        var model = new FakeChatClient();

        var choice = await _skills.ChooseAsync(Issue("bug", "pricing"), model, TestContext.Current.CancellationToken);

        choice.Skill.ShouldNotBeNull().Name.ShouldBe("pricing-review");
        choice.Route.ShouldBe(TriageSkills.Route.ExactTags);
        model.RecordedCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task WhenNoSkillCarriesEveryLabel_TheModelPicks()
    {
        var model = new FakeChatClient().EnqueueText("""{"selected": ["bug-triage"], "reasoning": "scripted"}""");

        var choice = await _skills.ChooseAsync(Issue("bug", "orders"), model, TestContext.Current.CancellationToken);

        choice.Skill.ShouldNotBeNull().Name.ShouldBe("bug-triage");
        choice.Route.ShouldBe(TriageSkills.Route.Model);
        model.RecordedCalls.Count.ShouldBe(1);
        model.RecordedCalls[0].Messages.Single().Text.ShouldContain("Triage issue #1");
    }

    [Theory]
    [InlineData("""{"selected": ["no-such-skill"], "reasoning": "scripted"}""")]
    [InlineData("this is not json")]
    public async Task WhenTheModelAnswerIsUnusable_TheSkillWithTheMostSharedLabelsIsTaken(string reply)
    {
        var model = new FakeChatClient().EnqueueText(reply);

        var choice = await _skills.ChooseAsync(Issue("bug", "pricing", "orders"), model, TestContext.Current.CancellationToken);

        choice.Skill.ShouldNotBeNull().Name.ShouldBe("pricing-review");
        choice.Route.ShouldBe(TriageSkills.Route.AnyTag);
    }

    [Fact]
    public async Task DeprecatedSkill_IsNeverChosen_EvenWhenItsLabelMatchesAndTheModelNamesIt()
    {
        var model = new FakeChatClient().EnqueueText("""{"selected": ["release-notes"], "reasoning": "scripted"}""");

        var choice = await _skills.ChooseAsync(Issue("docs"), model, TestContext.Current.CancellationToken);

        choice.Skill.ShouldBeNull();
        choice.Route.ShouldBe(TriageSkills.Route.None);
    }

    [Fact]
    public void Registry_ListsEverySkillOfTheFile_AndFindsOneByNameWhateverTheCase()
    {
        _skills.Registry.GetAll().Select(s => s.Name).ShouldBe(["bug-triage", "llm-hygiene", "pricing-review", "release-notes"]);
        _skills.Registry.GetByName("PRICING-REVIEW").ShouldNotBeNull().Tags.ShouldBe(["bug", "pricing"]);
    }

    static TriageIssue Issue(params string[] labels) => new(1, "Example", labels);
}
