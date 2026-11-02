#pragma warning disable AMPELUM_V09_PREVIEW
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Internal;
using SignalGrid.Ampelum.Host.Persistence;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Internal;

public sealed class AmpelumHostJsonContextTests
{
    [Fact]
    public void RebuildSummary_RoundTrips()
    {
        var value = new RebuildSummary(42, 5, 3, 2, TimeSpan.FromSeconds(1.5));
        var json = JsonSerializer.Serialize(value, AmpelumHostJsonContext.Default.RebuildSummary);
        var back = JsonSerializer.Deserialize(json, AmpelumHostJsonContext.Default.RebuildSummary);
        back!.AuditEntriesIndexed.ShouldBe(42);
        back.PromptVersionsIndexed.ShouldBe(5);
        back.OptimizerRunsCleared.ShouldBe(3);
    }

    [Fact]
    public void SseEnvelope_RoundTrips()
    {
        var value = new SseEnvelope("progress", "{\"step\":1}", 7);
        var json = JsonSerializer.Serialize(value, AmpelumHostJsonContext.Default.SseEnvelope);
        var back = JsonSerializer.Deserialize(json, AmpelumHostJsonContext.Default.SseEnvelope);
        back!.EventName.ShouldBe("progress");
        back.JsonPayload.ShouldBe("{\"step\":1}");
        back.Id.ShouldBe(7);
    }

    [Fact]
    public void OptimizerRunRow_RoundTrips()
    {
        var now = DateTimeOffset.UtcNow;
        var value = new OptimizerRunRow("run-1", "Mod", "bootstrap_few_shot",
            "succeeded", now, now.AddSeconds(10), 3, 3, "v-20260101-abc123", null);
        var json = JsonSerializer.Serialize(value, AmpelumHostJsonContext.Default.OptimizerRunRow);
        var back = JsonSerializer.Deserialize(json, AmpelumHostJsonContext.Default.OptimizerRunRow);
        back!.RunId.ShouldBe("run-1");
        back.State.ShouldBe("succeeded");
        back.TrialsCompleted.ShouldBe(3);
    }

    [Fact]
    public void PromptVersionFileShape_RoundTrips()
    {
        var meta = new PromptMeta("v-1", "Mod", "challenger",
            DateTimeOffset.UtcNow, null, null);
        var demos = new Dictionary<string, IReadOnlyList<DemoExampleShape>>
        {
            ["sub"] = new List<DemoExampleShape>
            {
                new(
                    new Dictionary<string, JsonElement> { ["Q"] = JsonSerializer.SerializeToElement("hi") },
                    new Dictionary<string, JsonElement> { ["A"] = JsonSerializer.SerializeToElement("bye") })
            }
        };
        var shape = new PromptVersionFileShape(1, meta, "Fake.FakeSig", demos,
            new Dictionary<string, string>());
        var json = JsonSerializer.Serialize(shape, AmpelumHostJsonContext.Default.PromptVersionFileShape);
        var back = JsonSerializer.Deserialize(json, AmpelumHostJsonContext.Default.PromptVersionFileShape);
        back!.Meta.VersionId.ShouldBe("v-1");
        back.DemoAssignments["sub"].Count.ShouldBe(1);
    }

    [Fact]
    public void PromptMeta_RoundTrips()
    {
        var value = new PromptMeta("v-2", "MyMod", "champion",
            DateTimeOffset.UtcNow, "v-1", "test note");
        var json = JsonSerializer.Serialize(value, AmpelumHostJsonContext.Default.PromptMeta);
        var back = JsonSerializer.Deserialize(json, AmpelumHostJsonContext.Default.PromptMeta);
        back!.VersionId.ShouldBe("v-2");
        back.ParentVersionId.ShouldBe("v-1");
        back.Note.ShouldBe("test note");
    }

    [Fact]
    public void PromptVersionRow_RoundTrips()
    {
        var now = DateTimeOffset.UtcNow;
        var value = new PromptVersionRow("v-abc", "Mod", "challenger", now, null, "/path/v.json", null);
        var json = JsonSerializer.Serialize(value, AmpelumHostJsonContext.Default.PromptVersionRow);
        var back = JsonSerializer.Deserialize(json, AmpelumHostJsonContext.Default.PromptVersionRow);
        back!.VersionId.ShouldBe("v-abc");
        back.FilePath.ShouldBe("/path/v.json");
    }

    [Fact]
    public void ModuleSnapshotRow_RoundTrips()
    {
        var now = DateTimeOffset.UtcNow;
        var value = new ModuleSnapshotRow("Mod", "Fake.FakeSig", false, now);
        var json = JsonSerializer.Serialize(value, AmpelumHostJsonContext.Default.ModuleSnapshotRow);
        var back = JsonSerializer.Deserialize(json, AmpelumHostJsonContext.Default.ModuleSnapshotRow);
        back!.ModuleName.ShouldBe("Mod");
        back.SupportsStream.ShouldBeFalse();
    }
}
