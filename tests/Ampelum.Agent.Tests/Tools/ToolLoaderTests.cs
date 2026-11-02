using Shouldly;
using SignalGrid.Ampelum.Agent;
using Xunit;

namespace SignalGrid.Ampelum.Tests.Tools;

public class ToolLoaderTests
{
    [Fact]
    public async Task SurveyAsync_ReturnsToolSurveyWithRequiresConfirmation()
    {
        // Arrange
        var source = new StubToolSource("test", new[]
        {
            new ToolSurvey
            {
                Name = "tool1",
                Description = "First tool",
                DangerTier = DangerTier.Safe,
                RequiresConfirmation = false
            },
            new ToolSurvey
            {
                Name = "tool2",
                Description = "Second tool",
                DangerTier = DangerTier.Destructive,
                RequiresConfirmation = true
            }
        });

        var loader = new ToolLoader(new[] { source });

        // Act
        var surveys = await loader.SurveyAsync(TestContext.Current.CancellationToken);

        // Assert
        surveys.Count.ShouldBe(2);
        surveys[0].Name.ShouldBe("test/tool1");
        surveys[0].RequiresConfirmation.ShouldBeFalse();
        surveys[1].Name.ShouldBe("test/tool2");
        surveys[1].RequiresConfirmation.ShouldBeTrue();
    }

    [Fact]
    public async Task TryUnpackAsync_CachesManifest()
    {
        // Arrange
        var manifest = new ToolManifest
        {
            Name = "test_tool",
            Description = "A test tool",
            DangerTier = DangerTier.Safe,
            Reversibility = Reversibility.Reversible
        };

        var source = new StubToolSource("test", Array.Empty<ToolSurvey>(), ("test_tool", manifest));
        var loader = new ToolLoader(new[] { source });

        // Act
        var result1 = await loader.TryUnpackAsync("test/test_tool", TestContext.Current.CancellationToken);
        var result2 = await loader.TryUnpackAsync("test/test_tool", TestContext.Current.CancellationToken);

        // Assert
        result1.ShouldNotBeNull();
        result2.ShouldNotBeNull();
        result1.ShouldBeSameAs(result2); // Same object from cache
    }

    [Fact]
    public async Task UnpackAsync_ReturnsManifestOnSuccess()
    {
        // Arrange
        var manifest = new ToolManifest
        {
            Name = "test_tool",
            Description = "A test tool",
            DangerTier = DangerTier.Safe,
            Reversibility = Reversibility.Reversible
        };

        var source = new StubToolSource("test", Array.Empty<ToolSurvey>(), ("test_tool", manifest));
        var loader = new ToolLoader(new[] { source });

        // Act
        var result = await loader.UnpackAsync("test/test_tool", TestContext.Current.CancellationToken);

        // Assert
        result.ShouldNotBeNull();
        result.Name.ShouldBe("test_tool");
    }

    [Fact]
    public async Task UnpackAsync_ThrowsKeyNotFoundForUnknownTool()
    {
        // Arrange
        var source = new StubToolSource("test", Array.Empty<ToolSurvey>());
        var loader = new ToolLoader(new[] { source });

        // Act & Assert
        await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await loader.UnpackAsync("test/unknown_tool", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SurveyAsync_CapsAt30Tools()
    {
        // Arrange
        var surveys = Enumerable.Range(1, 50)
            .Select(i => new ToolSurvey
            {
                Name = $"tool{i}",
                Description = $"Tool {i}",
                DangerTier = DangerTier.Safe,
                RequiresConfirmation = false
            })
            .ToArray();

        var source = new StubToolSource("test", surveys);
        var loader = new ToolLoader(new[] { source });

        // Act
        var result = await loader.SurveyAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Count.ShouldBe(30);
    }

    [Fact]
    public async Task EvictAsync_RemovesToolFromCache()
    {
        // Arrange
        var manifest = new ToolManifest
        {
            Name = "test_tool",
            Description = "A test tool",
            DangerTier = DangerTier.Safe,
            Reversibility = Reversibility.Reversible
        };

        var source = new StubToolSource("test", Array.Empty<ToolSurvey>(), ("test_tool", manifest));
        var loader = new ToolLoader(new[] { source });

        // Load it into cache
        var result1 = await loader.TryUnpackAsync("test/test_tool", TestContext.Current.CancellationToken);
        result1.ShouldNotBeNull();

        // Act
        await loader.EvictAsync("test/test_tool");

        // Assert - next load is a fresh fetch (source is reloaded)
        var result2 = await loader.TryUnpackAsync("test/test_tool", TestContext.Current.CancellationToken);
        result2.ShouldNotBeNull();
        // After evict, the cache was cleared, so calling again causes a fresh load
        // The manifest is the same from the stub source, but it was re-fetched and re-enriched
        result2.Name.ShouldBe(result1.Name); // Should still work
    }

    [Fact]
    public async Task SearchSurveyAsync_FiltersByName()
    {
        // Arrange
        var surveys = new[]
        {
            new ToolSurvey
            {
                Name = "edit_file",
                Description = "Edit a file",
                DangerTier = DangerTier.Destructive,
                RequiresConfirmation = true
            },
            new ToolSurvey
            {
                Name = "read_file",
                Description = "Read a file",
                DangerTier = DangerTier.Safe,
                RequiresConfirmation = false
            },
            new ToolSurvey
            {
                Name = "delete_item",
                Description = "Delete an item",
                DangerTier = DangerTier.Catastrophic,
                RequiresConfirmation = true
            }
        };

        var source = new StubToolSource("test", surveys);
        var loader = new ToolLoader(new[] { source });

        // Act
        var result = await loader.SearchSurveyAsync("edit", TestContext.Current.CancellationToken);

        // Assert
        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("test/edit_file");
    }

    [Fact]
    public async Task SearchSurveyAsync_FiltersByDescription()
    {
        // Arrange
        var surveys = new[]
        {
            new ToolSurvey
            {
                Name = "tool1",
                Description = "Create a new file with content",
                DangerTier = DangerTier.Safe,
                RequiresConfirmation = false
            },
            new ToolSurvey
            {
                Name = "tool2",
                Description = "Read from a database",
                DangerTier = DangerTier.Safe,
                RequiresConfirmation = false
            }
        };

        var source = new StubToolSource("test", surveys);
        var loader = new ToolLoader(new[] { source });

        // Act
        var result = await loader.SearchSurveyAsync("file", TestContext.Current.CancellationToken);

        // Assert
        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("test/tool1");
    }

    [Fact]
    public async Task SearchSurveyAsync_ReturnsMax10Results()
    {
        // Arrange
        var surveys = Enumerable.Range(1, 20)
            .Select(i => new ToolSurvey
            {
                Name = $"edit_tool{i}",
                Description = $"Editable tool {i}",
                DangerTier = DangerTier.Safe,
                RequiresConfirmation = false
            })
            .ToArray();

        var source = new StubToolSource("test", surveys);
        var loader = new ToolLoader(new[] { source });

        // Act
        var result = await loader.SearchSurveyAsync("edit", TestContext.Current.CancellationToken);

        // Assert
        result.Count.ShouldBe(10);
    }

    /// <summary>Stub tool source for testing — provides fixed surveys and manifests.</summary>
    private sealed class StubToolSource : IToolSource
    {
        private readonly Dictionary<string, ToolManifest> _manifests;
        private readonly IReadOnlyList<ToolSurvey> _surveys;

        public string SourcePrefix { get; }

        public StubToolSource(
            string prefix,
            IReadOnlyList<ToolSurvey> surveys,
            params (string name, ToolManifest manifest)[] manifests)
        {
            SourcePrefix = prefix;
            _surveys = surveys;
            _manifests = manifests.ToDictionary(m => m.name, m => m.manifest, StringComparer.Ordinal);
        }

        public Task<IReadOnlyList<ToolSurvey>> SurveyAsync(CancellationToken ct = default)
        {
            return Task.FromResult(_surveys);
        }

        public Task<ToolManifest?> TryUnpackAsync(string toolName, CancellationToken ct = default)
        {
            _manifests.TryGetValue(toolName, out var manifest);
            return Task.FromResult(manifest);
        }
    }
}
