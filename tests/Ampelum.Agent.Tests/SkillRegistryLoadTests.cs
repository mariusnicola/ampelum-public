using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class SkillRegistryLoadTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ampelum-skills-" + Guid.NewGuid().ToString("N")[..8]);

    public SkillRegistryLoadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    void Write(string name, string json) => File.WriteAllText(Path.Combine(_dir, name), json);

    [Fact]
    public async Task LoadAsync_ReadsAnArrayFileAndASingleObjectFile()
    {
        Write("many.json", """
            [
              { "name": "bug-triage", "version": "1.0.0", "tags": ["bug"], "systemPrompt": "triage" },
              { "name": "old", "version": "0.1.0", "deprecated": true }
            ]
            """);
        Write("one.json", """{ "name": "pricing", "version": "2.0.0", "description": "money", "tags": ["pricing"], "requires": ["read_file"] }""");
        var registry = new SkillRegistry();

        await registry.LoadAsync(_dir, Ct);

        registry.GetAll().Select(s => s.Name).ShouldBe(["bug-triage", "old", "pricing"]);
        var pricing = registry.GetByName("pricing").ShouldNotBeNull();
        pricing.Version.ShouldBe("2.0.0");
        pricing.Description.ShouldBe("money");
        pricing.Tags.ShouldBe(["pricing"]);
        pricing.Requires.ShouldBe(["read_file"]);
        registry.GetByName("bug-triage")!.SystemPrompt.ShouldBe("triage");
        registry.GetByName("old")!.Deprecated.ShouldBeTrue();
        registry.SelectByTags(["bug"]).Select(s => s.Name).ShouldBe(["bug-triage"]);
    }

    [Fact]
    public async Task LoadAsync_IgnoresOtherFiles_AndALaterFileReplacesASkillOfTheSameName()
    {
        Write("a.json", """{ "name": "same", "version": "1.0.0" }""");
        Write("b.json", """{ "name": "same", "version": "2.0.0" }""");
        Write("notes.txt", "not a skill");
        var registry = new SkillRegistry();

        await registry.LoadAsync(_dir, Ct);

        registry.GetAll().ShouldHaveSingleItem().Version.ShouldBe("2.0.0");
    }

    [Fact]
    public async Task LoadAsync_MissingDirectory_Throws()
    {
        await Should.ThrowAsync<DirectoryNotFoundException>(
            () => new SkillRegistry().LoadAsync(Path.Combine(_dir, "nope"), Ct));
    }

    [Theory]
    [InlineData("""[{"name":null,"version":"2"}]""")]
    [InlineData("""[null]""")]
    [InlineData("""[{"name":"","version":"2"}]""")]
    [InlineData("""[{"name":"x","version":null}]""")]
    [InlineData("""[{"name":"x","version":"  "}]""")]
    [InlineData("""null""")]
    public async Task LoadAsync_ManifestWithoutANameOrVersion_ThrowsJsonExceptionNamingTheFile_AndRegistersNothing(string badContent)
    {
        Write("a.json", """{ "name": "valid-before-the-bad-file", "version": "1.0.0" }""");
        Write("b.json", badContent);
        var registry = new SkillRegistry();

        var ex = await Should.ThrowAsync<System.Text.Json.JsonException>(() => registry.LoadAsync(_dir, Ct));

        ex.Message.ShouldContain("b.json");
        registry.GetAll().ShouldBeEmpty();
    }

    [Fact]
    public async Task LoadAsync_FileThatIsNotASkill_ThrowsAndRegistersNothingFromIt()
    {
        Write("a.json", """{ "name": "valid-before-the-bad-file", "version": "1.0.0" }""");
        Write("b.json", """{ "version": "1.0.0" }""");
        var registry = new SkillRegistry();

        await Should.ThrowAsync<System.Text.Json.JsonException>(() => registry.LoadAsync(_dir, Ct));

        registry.GetAll().ShouldBeEmpty();
    }
}
