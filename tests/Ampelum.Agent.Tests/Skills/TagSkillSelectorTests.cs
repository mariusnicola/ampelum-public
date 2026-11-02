using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class TagSkillSelectorTests
{
    private static SkillManifest ASkill(string name, string[] tags) =>
        new()
        {
            Name = name,
            Version = "1.0.0",
            Tags = tags
        };

    [Fact]
    public async Task Select_NoTags_ReturnsAll()
    {
        var skills = new[]
        {
            ASkill("a", ["csharp"]),
            ASkill("b", ["python"]),
            ASkill("c", ["rust"])
        };
        var selector = new TagSkillSelector();

        var result = await selector.SelectAsync(skills, [], TestContext.Current.CancellationToken);

        result.Count.ShouldBe(3);
        result.Select(s => s.Name).ShouldContain("a");
        result.Select(s => s.Name).ShouldContain("b");
        result.Select(s => s.Name).ShouldContain("c");
    }

    [Fact]
    public async Task Select_SingleTag_ReturnsMatching()
    {
        var skills = new[]
        {
            ASkill("csharp-skill", ["csharp"]),
            ASkill("python-skill", ["python"]),
            ASkill("multi-skill", ["csharp", "python"])
        };
        var selector = new TagSkillSelector();

        var result = await selector.SelectAsync(skills, ["csharp"], TestContext.Current.CancellationToken);

        result.Count.ShouldBe(2);
        result.Select(s => s.Name).ShouldContain("csharp-skill");
        result.Select(s => s.Name).ShouldContain("multi-skill");
    }

    [Fact]
    public async Task Select_MultipleTags_RequiresAll()
    {
        var skills = new[]
        {
            ASkill("fullstack", ["csharp", "python", "javascript"]),
            ASkill("csharp-only", ["csharp"]),
            ASkill("python-only", ["python"]),
            ASkill("csharp-python", ["csharp", "python"])
        };
        var selector = new TagSkillSelector();

        var result = await selector.SelectAsync(skills, ["csharp", "python"], TestContext.Current.CancellationToken);

        result.Count.ShouldBe(2);
        result.Select(s => s.Name).ShouldContain("fullstack");
        result.Select(s => s.Name).ShouldContain("csharp-python");
    }

    [Fact]
    public async Task Select_NoMatch_ReturnsEmpty()
    {
        var skills = new[]
        {
            ASkill("git-skill", ["git"]),
            ASkill("docker-skill", ["docker"])
        };
        var selector = new TagSkillSelector();

        var result = await selector.SelectAsync(skills, ["database"], TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task Select_SkillsWithSubsetTags_Excluded()
    {
        var skills = new[]
        {
            ASkill("fullstack", ["csharp", "python", "javascript"]),
            ASkill("partial", ["csharp"]),
            ASkill("unrelated", ["rust"])
        };
        var selector = new TagSkillSelector();

        var result = await selector.SelectAsync(skills, ["csharp", "python"], TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("fullstack");
    }

    [Fact]
    public async Task Select_CaseInsensitiveByDefault()
    {
        var skills = new[]
        {
            ASkill("dotnet-skill", ["CSharp"]),
            ASkill("py-skill", ["Python"])
        };
        var selector = new TagSkillSelector();

        var result = await selector.SelectAsync(skills, ["csharp"], TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("dotnet-skill");
    }

    [Fact]
    public async Task Select_CaseSensitiveWhenConfigured()
    {
        var skills = new[]
        {
            ASkill("dotnet-skill", ["CSharp"]),
            ASkill("py-skill", ["Python"])
        };
        var selector = new TagSkillSelector(StringComparison.Ordinal);

        var result = await selector.SelectAsync(skills, ["csharp"], TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
    }
}
