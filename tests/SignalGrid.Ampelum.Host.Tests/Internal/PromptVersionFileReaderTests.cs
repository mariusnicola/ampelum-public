#pragma warning disable AMPELUM_V09_PREVIEW
using System.Text.Json;
using Shouldly;
using Xunit;
using SignalGrid.Ampelum.Host.Internal;

namespace SignalGrid.Ampelum.Host.Tests.Internal;

public sealed class PromptVersionFileReaderTests : IAsyncLifetime
{
    private string _tempFile = null!;

    public async ValueTask InitializeAsync()
    {
        _tempFile = Path.GetTempFileName();
        // Clean up so we can use this path for our tests
        if (File.Exists(_tempFile))
            File.Delete(_tempFile);
    }

    public async ValueTask DisposeAsync()
    {
        if (File.Exists(_tempFile))
            File.Delete(_tempFile);
        var tmpFile = _tempFile + ".tmp";
        if (File.Exists(tmpFile))
            File.Delete(tmpFile);
    }

    [Fact]
    public async Task WriteAsync_ThenReadAsync_RoundTrips()
    {
        // Arrange
        var meta = new PromptMeta(
            VersionId: "v-12345",
            ModuleName: "Fake",
            Status: "challenger",
            CreatedAt: new DateTimeOffset(2026, 4, 27, 10, 30, 0, TimeSpan.Zero),
            ParentVersionId: "v-11111",
            Note: "Test version");

        var demoInputs = new Dictionary<string, JsonElement>
        {
            ["question"] = JsonSerializer.SerializeToElement("What is 2+2?"),
            ["format"] = JsonSerializer.SerializeToElement("json"),
        };

        var demoOutputs = new Dictionary<string, JsonElement>
        {
            ["answer"] = JsonSerializer.SerializeToElement("4"),
        };

        var demos = new Dictionary<string, IReadOnlyList<DemoExampleShape>>
        {
            ["default"] = new[]
            {
                new DemoExampleShape(demoInputs, demoOutputs),
            },
        };

        var overrides = new Dictionary<string, string>
        {
            ["temperature"] = "0.7",
        };

        var shape = new PromptVersionFileShape(
            Version: 1,
            Meta: meta,
            TProgramType: "FakeSig",
            DemoAssignments: demos,
            InstructionOverrides: overrides);

        // Act
        await PromptVersionFileReader.WriteAsync(_tempFile, shape, TestContext.Current.CancellationToken);
        var readBack = await PromptVersionFileReader.ReadAsync(_tempFile, TestContext.Current.CancellationToken);

        // Assert
        readBack.Version.ShouldBe(1);
        readBack.TProgramType.ShouldBe("FakeSig");

        readBack.Meta.VersionId.ShouldBe("v-12345");
        readBack.Meta.ModuleName.ShouldBe("Fake");
        readBack.Meta.Status.ShouldBe("challenger");
        readBack.Meta.CreatedAt.ShouldBe(new DateTimeOffset(2026, 4, 27, 10, 30, 0, TimeSpan.Zero));
        readBack.Meta.ParentVersionId.ShouldBe("v-11111");
        readBack.Meta.Note.ShouldBe("Test version");

        readBack.DemoAssignments.Keys.ShouldContain("default");
        var defaultDemos = readBack.DemoAssignments["default"];
        defaultDemos.Count.ShouldBe(1);

        var demoExample = defaultDemos[0];
        demoExample.Inputs.Keys.ShouldContain("question");
        demoExample.Inputs.Keys.ShouldContain("format");
        demoExample.Inputs["question"].GetString().ShouldBe("What is 2+2?");
        demoExample.Inputs["format"].GetString().ShouldBe("json");

        demoExample.Outputs.Keys.ShouldContain("answer");
        demoExample.Outputs["answer"].GetString().ShouldBe("4");

        readBack.InstructionOverrides["temperature"].ShouldBe("0.7");
    }

    [Fact]
    public async Task ReadAsync_MissingFile_Throws()
    {
        // Arrange
        var nonExistentPath = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid() + ".json");

        // Act & Assert
        await Should.ThrowAsync<FileNotFoundException>(async () =>
            await PromptVersionFileReader.ReadAsync(nonExistentPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteAsync_UsesTemporaryFile_MovesToFinal()
    {
        // Arrange
        var meta = new PromptMeta("v-1", "Test", "stable", DateTimeOffset.UtcNow, null, null);
        var shape = new PromptVersionFileShape(
            Version: 1,
            Meta: meta,
            TProgramType: "TestSig",
            DemoAssignments: new Dictionary<string, IReadOnlyList<DemoExampleShape>>(),
            InstructionOverrides: new Dictionary<string, string>());

        var tmpPath = _tempFile + ".tmp";
        if (File.Exists(tmpPath))
            File.Delete(tmpPath);

        // Act
        await PromptVersionFileReader.WriteAsync(_tempFile, shape, TestContext.Current.CancellationToken);

        // Assert
        File.Exists(_tempFile).ShouldBeTrue();
        File.Exists(tmpPath).ShouldBeFalse();
    }

    [Fact]
    public async Task ReadAsync_MissingMetaBlock_DefaultsStatusToUnknown()
    {
        // Arrange - Write a JSON file without the _meta block
        var jsonWithoutMeta = @"{
  ""version"": 1,
  ""tProgramType"": ""FakeSig"",
  ""demoAssignments"": {},
  ""instructionOverrides"": {}
}";
        await File.WriteAllTextAsync(_tempFile, jsonWithoutMeta, TestContext.Current.CancellationToken);

        // Act
        var readBack = await PromptVersionFileReader.ReadAsync(_tempFile, TestContext.Current.CancellationToken);

        // Assert
        readBack.Meta.VersionId.ShouldBe("unknown");
        readBack.Meta.ModuleName.ShouldBe("unknown");
        readBack.Meta.Status.ShouldBe("unknown");
        readBack.Meta.CreatedAt.ShouldBe(DateTimeOffset.MinValue);
        readBack.Meta.ParentVersionId.ShouldBeNull();
        readBack.Meta.Note.ShouldBeNull();
    }
}
