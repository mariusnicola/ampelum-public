using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class SkillRegistryTests
{
    private static SkillManifest ASkill(string name, string description = "", bool deprecated = false) =>
        new()
        {
            Name = name,
            Version = "1.0.0",
            Description = description,
            Tags = [],
            Deprecated = deprecated
        };

    private static SkillManifest ASkill(string name, string[] tags, string description = "", bool deprecated = false) =>
        new()
        {
            Name = name,
            Version = "1.0.0",
            Description = description,
            Tags = tags,
            Deprecated = deprecated
        };

    // ── Register ────────────────────────────────────────────────────

    [Fact]
    public void Register_NewSkill_CanBeRetrieved()
    {
        var registry = new SkillRegistry();
        registry.Register(ASkill("list-files", "List files"));

        var skill = registry.GetByName("list-files");
        skill.ShouldNotBeNull();
        skill!.Name.ShouldBe("list-files");
    }

    [Fact]
    public void Register_OverwritesExistingSkill()
    {
        var registry = new SkillRegistry();
        registry.Register(ASkill("skill1", "Old"));

        registry.Register(new SkillManifest
        {
            Name = "skill1",
            Version = "2.0.0",
            Description = "New"
        });

        var skill = registry.GetByName("skill1");
        skill!.Description.ShouldBe("New");
        skill.Version.ShouldBe("2.0.0");
    }

    // ── GetByName ───────────────────────────────────────────────────

    [Fact]
    public void GetByName_UnknownSkill_ReturnsNull()
    {
        var registry = new SkillRegistry();

        var skill = registry.GetByName("nonexistent");

        skill.ShouldBeNull();
    }

    [Fact]
    public void GetByName_CaseInsensitive_ReturnsMatch()
    {
        var registry = new SkillRegistry();
        registry.Register(ASkill("ReadFile"));

        var skill = registry.GetByName("readfile");

        skill.ShouldNotBeNull();
        skill!.Name.ShouldBe("ReadFile");
    }

    // ── GetAll ──────────────────────────────────────────────────────

    [Fact]
    public void GetAll_ReturnsAllRegisteredSkills_SortedByName()
    {
        var registry = new SkillRegistry();
        registry.Register(ASkill("z-skill"));
        registry.Register(ASkill("a-skill"));
        registry.Register(ASkill("m-skill"));

        var all = registry.GetAll();

        all.Count.ShouldBe(3);
        all[0].Name.ShouldBe("a-skill");
        all[1].Name.ShouldBe("m-skill");
        all[2].Name.ShouldBe("z-skill");
    }

    [Fact]
    public void GetAll_EmptyRegistry_ReturnsEmpty()
    {
        var registry = new SkillRegistry();

        var all = registry.GetAll();

        all.ShouldBeEmpty();
    }

    // ── SelectByTags ────────────────────────────────────────────────

    [Fact]
    public void SelectByTags_ExcludesDeprecated()
    {
        var registry = new SkillRegistry();
        registry.Register(ASkill("active", ["data"], deprecated: false));
        registry.Register(ASkill("retired", ["data"], deprecated: true));

        var result = registry.SelectByTags(["data"]);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("active");
    }

    [Fact]
    public void SelectByTags_MatchByTag_ReturnsMatching()
    {
        var registry = new SkillRegistry();
        registry.Register(ASkill("repo-read", tags: ["git", "read"]));
        registry.Register(ASkill("file-read", tags: ["filesystem", "read"]));
        registry.Register(ASkill("web-search", tags: ["web"]));

        var result = registry.SelectByTags(["read"]);

        result.Count.ShouldBe(2);
        result.Select(s => s.Name).ShouldContain("repo-read");
        result.Select(s => s.Name).ShouldContain("file-read");
    }

    [Fact]
    public void SelectByTags_EmptyTags_ReturnsEmpty()
    {
        var registry = new SkillRegistry();
        registry.Register(ASkill("anything", tags: ["data"]));

        var result = registry.SelectByTags([]);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void SelectByTags_NoMatch_ReturnsEmpty()
    {
        var registry = new SkillRegistry();
        registry.Register(ASkill("git-push", tags: ["git"]));

        var result = registry.SelectByTags(["database"]);

        result.ShouldBeEmpty();
    }

    // ── Match ───────────────────────────────────────────────────────

    [Fact]
    public void Match_ExactTaskType_ReturnsSkill()
    {
        var registry = new SkillRegistry();
        registry.Register(ASkill("deploy", tags: ["git", "ci"]));

        var task = new TaskNode { Id = "t1", Description = "Deploy", TaskType = "deploy" };
        var result = registry.Match(task);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("deploy");
    }

    [Fact]
    public void Match_FallbackToTags_ReturnsSkills()
    {
        var registry = new SkillRegistry();
        registry.Register(ASkill("git-clone", tags: ["git"]));
        registry.Register(ASkill("docker-build", tags: ["docker"]));

        var task = new TaskNode { Id = "t1", Description = "Git clone", TaskType = "git" };
        var result = registry.Match(task);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("git-clone");
    }

    [Fact]
    public void Match_WithNoMatch_ReturnsEmpty()
    {
        var registry = new SkillRegistry();
        registry.Register(ASkill("npm-build", tags: ["node"]));

        var task = new TaskNode { Id = "t1", Description = "Python", TaskType = "python" };
        var result = registry.Match(task);

        result.ShouldBeEmpty();
    }

    // ── Deprecate ───────────────────────────────────────────────────

    [Fact]
    public void Deprecate_MarksSkillDeprecated()
    {
        var registry = new SkillRegistry();
        registry.Register(ASkill("old-tool"));

        registry.Deprecate("old-tool");

        registry.GetByName("old-tool")!.Deprecated.ShouldBeTrue();
    }

    [Fact]
    public void Deprecate_WrongName_DoesNotThrow()
    {
        var registry = new SkillRegistry();

        Should.NotThrow(() => registry.Deprecate("does-not-exist"));
    }

    [Fact]
    public void GetAll_ReturnsDeprecatedSkills()
    {
        var registry = new SkillRegistry();
        registry.Register(ASkill("keep"));
        registry.Register(ASkill("retire"));
        registry.Deprecate("retire");

        var all = registry.GetAll();

        // GetAll returns all regardless of deprecated status
        all.Count.ShouldBe(2);
    }
}