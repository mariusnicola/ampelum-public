#pragma warning disable AMPELUM_V09_PREVIEW
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Contracts;

public sealed class PromptRoundTripTests
{
    private static readonly JsonSerializerOptions Options =
        AmpelumHostContractsJsonContext.Default.Options;

    private static DemoExample MakeExample() => new(
        Inputs: new Dictionary<string, JsonElement>
        {
            ["q"] = JsonDocument.Parse("\"hi\"").RootElement,
        }.AsReadOnly(),
        Outputs: new Dictionary<string, JsonElement>
        {
            ["a"] = JsonDocument.Parse("\"hello\"").RootElement,
        }.AsReadOnly());

    private static PromptVersion MakeVersion() => new(
        VersionId: "v1",
        ModuleName: "qa",
        CreatedAt: new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero),
        Status: PromptStatus.Active,
        DemoAssignments: new Dictionary<string, IReadOnlyList<DemoExample>>
        {
            ["qa"] = new[] { MakeExample() },
        }.AsReadOnly(),
        InstructionOverrides: new Dictionary<string, string>
        {
            ["system"] = "Be concise.",
        }.AsReadOnly(),
        ParentVersionId: null,
        Note: "initial");

    [Fact]
    public void DemoExample_RoundTrip()
    {
        var src  = MakeExample();
        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<DemoExample>(json, Options);
        dst!.Inputs["q"].GetString().ShouldBe("hi");
        dst.Outputs["a"].GetString().ShouldBe("hello");
    }

    [Fact]
    public void PromptVersion_RoundTrip()
    {
        var src  = MakeVersion();
        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<PromptVersion>(json, Options);
        dst!.VersionId.ShouldBe("v1");
        dst.Status.ShouldBe(PromptStatus.Active);
        dst.DemoAssignments["qa"].Count.ShouldBe(1);
        dst.InstructionOverrides["system"].ShouldBe("Be concise.");
        dst.Note.ShouldBe("initial");
        dst.ParentVersionId.ShouldBeNull();
    }

    [Fact]
    public void PromptVersion_ParentVersionId_OmittedWhenNull()
    {
        var json = JsonSerializer.Serialize(MakeVersion(), Options);
        json.ShouldNotContain("parentVersionId");
    }

    [Fact]
    public void PromptVersionList_RoundTrip()
    {
        var src  = new PromptVersionList("qa", new[] { MakeVersion() });
        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<PromptVersionList>(json, Options);
        dst!.ModuleName.ShouldBe("qa");
        dst.Versions.Count.ShouldBe(1);
    }

    [Fact]
    public void PromptIndexResponse_RoundTrip()
    {
        var src  = new PromptIndexResponse(new[] { "qa", "coder" });
        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<PromptIndexResponse>(json, Options);
        dst!.ModuleNames.Count.ShouldBe(2);
        dst.ModuleNames[0].ShouldBe("qa");
    }

    [Fact]
    public void CreatePromptVersionRequest_RoundTrip()
    {
        var src = new CreatePromptVersionRequest(
            DemoAssignments: new Dictionary<string, IReadOnlyList<DemoExample>>
            {
                ["qa"] = new[] { MakeExample() },
            }.AsReadOnly(),
            InstructionOverrides: new Dictionary<string, string>().AsReadOnly(),
            ParentVersionId: "v0",
            Note: "upgrade");
        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<CreatePromptVersionRequest>(json, Options);
        dst!.ParentVersionId.ShouldBe("v0");
        dst.Note.ShouldBe("upgrade");
        dst.DemoAssignments["qa"].Count.ShouldBe(1);
    }

    [Fact]
    public void CreatePromptVersionRequest_OptionalFields_OmittedWhenNull()
    {
        var src  = new CreatePromptVersionRequest(
            new Dictionary<string, IReadOnlyList<DemoExample>>().AsReadOnly(),
            new Dictionary<string, string>().AsReadOnly());
        var json = JsonSerializer.Serialize(src, Options);
        json.ShouldNotContain("parentVersionId");
        json.ShouldNotContain("note");
    }

    [Fact]
    public void PromoteRequest_RoundTrip()
    {
        var src  = new PromoteRequest("v2");
        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<PromoteRequest>(json, Options);
        dst!.VersionId.ShouldBe("v2");
    }

    [Fact]
    public void PromoteResponse_RoundTrip_WithPrevious()
    {
        var src  = new PromoteResponse("qa", "v2", "v1");
        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<PromoteResponse>(json, Options);
        dst!.ModuleName.ShouldBe("qa");
        dst.ActiveVersionId.ShouldBe("v2");
        dst.PreviousActiveVersionId.ShouldBe("v1");
    }

    [Fact]
    public void PromoteResponse_PreviousActiveVersionId_OmittedWhenNull()
    {
        var src  = new PromoteResponse("qa", "v1", null);
        var json = JsonSerializer.Serialize(src, Options);
        json.ShouldNotContain("previousActiveVersionId");
    }
}
