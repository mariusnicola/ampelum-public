using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

[Collection("Orchestration")]
public class JsonFileProjectRegistryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"portfolio-{Guid.NewGuid():N}.json");

    private static WorkspaceContext MakeWorkspace(string name, string? rootPath = null) =>
        new()
        {
            Id = WorkspaceId.New(name),
            Label = name,
            RootPath = rootPath ?? string.Empty,
            PriorityWeight = 70,
            MaxBlastRadius = new BlastRadiusScope(),
            TokenStore = new InMemoryTokenStore(),
            SecretStore = new InMemorySecretStore(),
            Skills = new SkillRegistry(),
        };

    [Fact]
    public void Register_PersistsToDisk()
    {
        var registry = new JsonFileProjectRegistry(_path);
        registry.Register(MakeWorkspace("project-a"));
        File.Exists(_path).ShouldBeTrue();
    }

    [Fact]
    public void RoundTrip_RestoresWorkspace()
    {
        var registry1 = new JsonFileProjectRegistry(_path);
        registry1.Register(MakeWorkspace("restored"));

        var registry2 = new JsonFileProjectRegistry(_path);
        registry2.IsEmpty.ShouldBeFalse();
        var ws = registry2.Get(WorkspaceId.New("restored"));
        ws.ShouldNotBeNull();
        ws!.Label.ShouldBe("restored");
        ws.PriorityWeight.ShouldBe(70);
    }

    [Fact]
    public void Register_DuplicateId_Throws()
    {
        var registry = new JsonFileProjectRegistry(_path);
        registry.Register(MakeWorkspace("dup"));
        Should.Throw<DuplicateWorkspaceException>(() => registry.Register(MakeWorkspace("dup")));
    }

    [Fact]
    public async Task Deregister_PersistsRemoval()
    {
        var registry1 = new JsonFileProjectRegistry(_path);
        registry1.Register(MakeWorkspace("to-remove"));
        await registry1.DeregisterAsync(WorkspaceId.New("to-remove"));

        var registry2 = new JsonFileProjectRegistry(_path);
        registry2.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Load_CorruptFile_StartsEmpty()
    {
        File.WriteAllText(_path, "{ invalid json }");
        var registry = new JsonFileProjectRegistry(_path);
        registry.IsEmpty.ShouldBeTrue();
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}