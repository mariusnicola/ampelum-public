using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Audit;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Audit;

public sealed class AuditEntryTests
{
    static AuditEntry MakeMinimal() => new()
    {
        Sequence = 0,
        Timestamp = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero),
        ModuleName = "Predict<SimpleSig>",
        ModelId = "test-model",
        Inputs  = new Dictionary<string, object?> { ["q"] = "hi" },
        Outputs = new Dictionary<string, object?> { ["a"] = "hello" },
        PromptTokens = 12,
        CompletionTokens = 4,
        PrevHash = new byte[32],
        EntryHash = Enumerable.Repeat((byte)0xAB, 32).ToArray(),
    };

    static AuditEntry MakeMinimalEntry() => new()
    {
        Sequence = 0, Timestamp = DateTimeOffset.UnixEpoch,
        ModuleName = "M", ModelId = "m",
        Inputs = new Dictionary<string, object?>(),
        Outputs = new Dictionary<string, object?>(),
        PromptTokens = 0, CompletionTokens = 0,
        PrevHash = new byte[32], EntryHash = new byte[32],
    };

    [Fact]
    public void Construct_AllRequiredFieldsSet_Succeeds()
    {
        var entry = MakeMinimal();
        entry.Sequence.ShouldBe(0);
        entry.ModuleName.ShouldBe("Predict<SimpleSig>");
        entry.ModelId.ShouldBe("test-model");
        entry.PromptTokens.ShouldBe(12);
        entry.CompletionTokens.ShouldBe(4);
        entry.PrevHash.Length.ShouldBe(32);
        entry.EntryHash.Length.ShouldBe(32);
    }

    [Fact]
    public void With_Expression_ProducesNewInstance()
    {
        var a = MakeMinimal();
        var b = a with { Sequence = 1 };
        b.Sequence.ShouldBe(1);
        a.Sequence.ShouldBe(0);
        ReferenceEquals(a, b).ShouldBeFalse();
    }

    [Fact]
    public void RecordEquality_StructuralOnFields()
    {
        var a = MakeMinimal();
        var b = MakeMinimal();
        a.Equals(b).ShouldBeFalse();
    }

    [Fact]
    public void EventType_DefaultsToForwardCompleted()
    {
        var e = MakeMinimal();
        e.EventType.ShouldBe("forward.completed");
    }

    [Fact]
    public void EventType_OverrideViaInit_Roundtrips()
    {
        var e = MakeMinimal() with { EventType = "agent.handoff" };
        e.EventType.ShouldBe("agent.handoff");
    }

    [Fact]
    public void SchemaVersion_DefaultsTo1_ForBackwardCompat()
    {
        // alpha.12: pre-existing serialized rows have no SchemaVersion field, so STJ
        // resolves the missing field to the C# default. That default MUST be 1 so legacy
        // rows verify under the v1 algorithm without a custom converter.
        var e = MakeMinimal();
        e.SchemaVersion.ShouldBe(1);
    }

    [Fact]
    public void SchemaVersion_OverrideViaInit_Roundtrips()
    {
        var e = MakeMinimal() with { SchemaVersion = 2 };
        e.SchemaVersion.ShouldBe(2);
    }

    [Fact]
    public void TraceId_DefaultsToEmptyString_WhenNotSet()
    {
        var entry = new AuditEntry
        {
            Sequence = 0, Timestamp = DateTimeOffset.UnixEpoch,
            ModuleName = "M", ModelId = "m",
            Inputs = new Dictionary<string, object?>(),
            Outputs = new Dictionary<string, object?>(),
            PromptTokens = 0, CompletionTokens = 0,
            PrevHash = new byte[32], EntryHash = new byte[32],
        };
        entry.TraceId.ShouldBe("");
    }

    [Fact]
    public void TraceId_IsPropagatedByRecordWith()
    {
        var original = MakeMinimalEntry() with { TraceId = "0123456789abcdef0123456789abcdef" };
        var clone = original with { Sequence = 99 };
        clone.TraceId.ShouldBe("0123456789abcdef0123456789abcdef");
    }

    [Fact]
    public void TraceId_IsInitOnly_DetectableViaPropertyInfo()
    {
        var prop = typeof(AuditEntry).GetProperty(nameof(AuditEntry.TraceId))!;
        prop.CanWrite.ShouldBeTrue();
        var setter = prop.SetMethod!;
        setter.ReturnParameter.GetRequiredCustomModifiers()
            .ShouldContain(typeof(System.Runtime.CompilerServices.IsExternalInit));
    }

    [Fact]
    public void TraceId_RoundTripsThroughStj_OnV3Entry()
    {
        var entry = MakeMinimalEntry() with
        {
            SchemaVersion = 3,
            TraceId = "deadbeefdeadbeefdeadbeefdeadbeef",
        };
        var json = System.Text.Json.JsonSerializer.Serialize(entry, AuditEntryJson.Default.AuditEntry);
        json.ShouldContain("\"TraceId\":\"deadbeefdeadbeefdeadbeefdeadbeef\"");
        var parsed = System.Text.Json.JsonSerializer.Deserialize(json, AuditEntryJson.Default.AuditEntry)!;
        parsed.TraceId.ShouldBe("deadbeefdeadbeefdeadbeefdeadbeef");
    }

    [Fact]
    public void TraceId_DeserializesToEmptyString_WhenMissingFromV2Json()
    {
        var v2Json = """{"SchemaVersion":2,"EventType":"forward.completed","Sequence":0,"Timestamp":"1970-01-01T00:00:00+00:00","ModuleName":"M","ModelId":"m","Inputs":{},"Outputs":{},"PromptTokens":0,"CompletionTokens":0,"PrevHash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=","EntryHash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}""";
        var parsed = System.Text.Json.JsonSerializer.Deserialize(v2Json, AuditEntryJson.Default.AuditEntry)!;
        parsed.TraceId.ShouldBe("");
    }
}
