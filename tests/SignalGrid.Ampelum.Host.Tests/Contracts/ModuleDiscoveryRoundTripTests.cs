#pragma warning disable AMPELUM_V09_PREVIEW
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Contracts;

public sealed class ModuleDiscoveryRoundTripTests
{
    private static readonly JsonSerializerOptions Options =
        AmpelumHostContractsJsonContext.Default.Options;

    [Fact]
    public void FieldDescriptor_RoundTrip_AllFields()
    {
        var src  = new FieldDescriptor("question", "System.String", "The question to ask", Required: true);
        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<FieldDescriptor>(json, Options);
        dst.ShouldBe(src);
    }

    [Fact]
    public void FieldDescriptor_Description_OmittedWhenNull()
    {
        var src  = new FieldDescriptor("x", "System.Int32", null, false);
        var json = JsonSerializer.Serialize(src, Options);
        json.ShouldNotContain("description");
        var dst = JsonSerializer.Deserialize<FieldDescriptor>(json, Options);
        dst!.Description.ShouldBeNull();
    }

    [Fact]
    public void ModuleDescriptor_RoundTrip()
    {
        var field = new FieldDescriptor("q", "System.String", null, true);
        var src = new ModuleDescriptor(
            Name: "qa",
            SignatureType: "QaSig",
            Description: "Q&A module",
            Inputs: new[] { field },
            Outputs: new[] { field },
            SupportsStreaming: true);
        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<ModuleDescriptor>(json, Options);
        dst!.Name.ShouldBe("qa");
        dst.Inputs.Count.ShouldBe(1);
        dst.Outputs.Count.ShouldBe(1);
        dst.SupportsStreaming.ShouldBeTrue();
    }

    [Fact]
    public void ModuleListResponse_RoundTrip()
    {
        var field   = new FieldDescriptor("a", "System.String", null, false);
        var module  = new ModuleDescriptor("m", "S", "desc", new[] { field }, Array.Empty<FieldDescriptor>(), false);
        var src     = new ModuleListResponse(new[] { module });
        var json    = JsonSerializer.Serialize(src, Options);
        var dst     = JsonSerializer.Deserialize<ModuleListResponse>(json, Options);
        dst!.Modules.Count.ShouldBe(1);
        dst.Modules[0].Name.ShouldBe("m");
    }
}
