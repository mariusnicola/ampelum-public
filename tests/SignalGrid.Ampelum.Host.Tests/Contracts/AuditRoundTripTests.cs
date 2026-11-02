#pragma warning disable AMPELUM_V09_PREVIEW
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Contracts;

public sealed class AuditRoundTripTests
{
    private static readonly JsonSerializerOptions Options =
        AmpelumHostContractsJsonContext.Default.Options;

    private static AuditEntryDto MakeEntry() => new(
        Sequence: 1,
        Timestamp: new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero),
        TraceId: "tr-1",
        ModuleName: "qa",
        EventType: "forward",
        ModelId: "gpt-4",
        Inputs: new Dictionary<string, JsonElement>
        {
            ["q"] = JsonDocument.Parse("\"hello\"").RootElement,
        }.AsReadOnly(),
        Outputs: new Dictionary<string, JsonElement>
        {
            ["a"] = JsonDocument.Parse("\"world\"").RootElement,
        }.AsReadOnly(),
        PromptTokens: 10,
        CompletionTokens: 5,
        EntryHashHex: new string('a', 64),
        PrevHashHex: new string('0', 64),
        SchemaVersion: 3);

    [Fact]
    public void AuditEntryDto_RoundTrip()
    {
        var src  = MakeEntry();
        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<AuditEntryDto>(json, Options);
        dst!.Sequence.ShouldBe(1L);
        dst.TraceId.ShouldBe("tr-1");
        dst.EntryHashHex.ShouldBe(new string('a', 64));
        dst.SchemaVersion.ShouldBe(3);
        dst.Inputs["q"].GetString().ShouldBe("hello");
    }

    [Fact]
    public void AuditEntryDto_CamelCase()
    {
        var json = JsonSerializer.Serialize(MakeEntry(), Options);
        json.ShouldContain("\"traceId\"");
        json.ShouldContain("\"moduleName\"");
        json.ShouldContain("\"entryHashHex\"");
        json.Contains("\"TraceId\"").ShouldBeFalse();
    }

    [Fact]
    public void AuditPage_RoundTrip_WithCursor()
    {
        var src  = new AuditPage(new[] { MakeEntry() }, NextSinceSequence: 2L);
        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<AuditPage>(json, Options);
        dst!.Entries.Count.ShouldBe(1);
        dst.NextSinceSequence.ShouldBe(2L);
    }

    [Fact]
    public void AuditPage_RoundTrip_NoCursor()
    {
        var src  = new AuditPage(Array.Empty<AuditEntryDto>(), NextSinceSequence: null);
        var json = JsonSerializer.Serialize(src, Options);
        json.ShouldNotContain("nextSinceSequence");
        var dst = JsonSerializer.Deserialize<AuditPage>(json, Options);
        dst!.NextSinceSequence.ShouldBeNull();
    }

    [Fact]
    public void AuditVerifyResponse_Ok_RoundTrip()
    {
        var src  = new AuditVerifyResponse(Ok: true, BrokenAt: null, Reason: "chain intact");
        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<AuditVerifyResponse>(json, Options);
        dst!.Ok.ShouldBeTrue();
        dst.BrokenAt.ShouldBeNull();
        dst.Reason.ShouldBe("chain intact");
    }

    [Fact]
    public void AuditVerifyResponse_Broken_RoundTrip()
    {
        var src  = new AuditVerifyResponse(Ok: false, BrokenAt: 42L, Reason: "hash mismatch");
        var json = JsonSerializer.Serialize(src, Options);
        var dst  = JsonSerializer.Deserialize<AuditVerifyResponse>(json, Options);
        dst!.Ok.ShouldBeFalse();
        dst.BrokenAt.ShouldBe(42L);
    }
}
