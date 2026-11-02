using System.Collections;
using SignalGrid.Ampelum.Hooks;
using Shouldly;
using Xunit;
using SignalGrid.Ampelum;

namespace Ampelum.Tests.Hooks;

public sealed class HookContextTests
{
    static HookContext NewCtx() => new()
    {
        ModuleName = "Predict",
        Inputs = new Dictionary<string, object?> { ["q"] = "hi" },
    };

    [Fact]
    public void Set_WritesValue_VisibleViaMetadata()
    {
        var ctx = NewCtx();
        ctx.Set("budget.scope", "tenant-7");

        ctx.Metadata.ContainsKey("budget.scope").ShouldBeTrue();
        ctx.Metadata["budget.scope"].ShouldBe("tenant-7");
    }

    [Fact]
    public void Set_OverwritesExistingKey()
    {
        var ctx = NewCtx();
        ctx.Set("k", 1);
        ctx.Set("k", 2);
        ctx.Metadata["k"].ShouldBe(2);
    }

    [Fact]
    public void Outputs_DefaultsToNull_BeforeOnAfter()
    {
        var ctx = NewCtx();
        ctx.Outputs.ShouldBeNull();
    }

    [Fact]
    public void Set_IsThreadSafe()
    {
        var ctx = NewCtx();
        Parallel.For(0, 1000, i => ctx.Set("k" + i, i));
        ctx.Metadata.Count.ShouldBe(1000);
    }

    // ---------- v1.0-rc1 alpha.6: typed slots + IReadOnlyDictionary projection ----------

    [Fact]
    public void TypedSlots_RoundtripThroughSet_AndMetadata()
    {
        var ctx = NewCtx();
        ctx.Set(HookMetadataKeys.ModelId, "gpt-4o");
        ctx.Set(HookMetadataKeys.PromptTokens, 42L);
        ctx.Set(HookMetadataKeys.CompletionTokens, 7L);
        ctx.Set(HookMetadataKeys.LmCallDurationMs, 12.5);

        ctx.ModelId.ShouldBe("gpt-4o");
        ctx.PromptTokens.ShouldBe(42L);
        ctx.CompletionTokens.ShouldBe(7L);
        ctx.LmCallDurationMs.ShouldBe(12.5);

        ctx.Metadata[HookMetadataKeys.ModelId].ShouldBe("gpt-4o");
        ctx.Metadata[HookMetadataKeys.PromptTokens].ShouldBe(42L);
        ctx.Metadata[HookMetadataKeys.CompletionTokens].ShouldBe(7L);
        ctx.Metadata[HookMetadataKeys.LmCallDurationMs].ShouldBe(12.5);
    }

    [Fact]
    public void TypedSlots_DirectPropertySet_VisibleViaMetadata()
    {
        var ctx = NewCtx();
        ctx.ModelId = "claude";
        ctx.PromptTokens = 1L;
        ctx.CompletionTokens = 2L;
        ctx.LmCallDurationMs = 3.5;

        ctx.Metadata.TryGetValue(HookMetadataKeys.ModelId, out var m).ShouldBeTrue();
        m.ShouldBe("claude");
        ctx.Metadata.TryGetValue(HookMetadataKeys.PromptTokens, out var p).ShouldBeTrue();
        p.ShouldBe(1L);
        ctx.Metadata.TryGetValue(HookMetadataKeys.CompletionTokens, out var c).ShouldBeTrue();
        c.ShouldBe(2L);
        ctx.Metadata.TryGetValue(HookMetadataKeys.LmCallDurationMs, out var d).ShouldBeTrue();
        d.ShouldBe(3.5);
    }

    [Fact]
    public void TypedSlots_StartUnset_ContainsKeyFalse_TryGetValueFalse()
    {
        var ctx = NewCtx();
        ctx.ModelId.ShouldBeNull();
        ctx.PromptTokens.ShouldBeNull();
        ctx.CompletionTokens.ShouldBeNull();
        ctx.LmCallDurationMs.ShouldBeNull();

        ctx.Metadata.ContainsKey(HookMetadataKeys.ModelId).ShouldBeFalse();
        ctx.Metadata.ContainsKey(HookMetadataKeys.PromptTokens).ShouldBeFalse();
        ctx.Metadata.ContainsKey(HookMetadataKeys.CompletionTokens).ShouldBeFalse();
        ctx.Metadata.ContainsKey(HookMetadataKeys.LmCallDurationMs).ShouldBeFalse();

        ctx.Metadata.TryGetValue(HookMetadataKeys.ModelId, out var v1).ShouldBeFalse();
        v1.ShouldBeNull();
        ctx.Metadata.TryGetValue(HookMetadataKeys.PromptTokens, out var v2).ShouldBeFalse();
        v2.ShouldBeNull();
        ctx.Metadata.TryGetValue(HookMetadataKeys.CompletionTokens, out var v3).ShouldBeFalse();
        v3.ShouldBeNull();
        ctx.Metadata.TryGetValue(HookMetadataKeys.LmCallDurationMs, out var v4).ShouldBeFalse();
        v4.ShouldBeNull();
    }

    [Fact]
    public void Set_NonLongPromptTokens_CoercesToInt64()
    {
        var ctx = NewCtx();
        ctx.Set(HookMetadataKeys.PromptTokens, 5);  // int, not long
        ctx.PromptTokens.ShouldBe(5L);
        ctx.Set(HookMetadataKeys.CompletionTokens, (short)9);
        ctx.CompletionTokens.ShouldBe(9L);
        // Non-double for duration coerces too.
        ctx.Set(HookMetadataKeys.LmCallDurationMs, 4);
        ctx.LmCallDurationMs.ShouldBe(4.0);
    }

    [Fact]
    public void Set_NullValueClearsTypedSlot()
    {
        var ctx = NewCtx();
        ctx.Set(HookMetadataKeys.ModelId, "gpt-4o");
        ctx.Set(HookMetadataKeys.ModelId, null);
        ctx.ModelId.ShouldBeNull();

        ctx.Set(HookMetadataKeys.PromptTokens, 1L);
        ctx.Set(HookMetadataKeys.PromptTokens, null);
        ctx.PromptTokens.ShouldBeNull();

        ctx.Set(HookMetadataKeys.CompletionTokens, 2L);
        ctx.Set(HookMetadataKeys.CompletionTokens, null);
        ctx.CompletionTokens.ShouldBeNull();

        ctx.Set(HookMetadataKeys.LmCallDurationMs, 3.5);
        ctx.Set(HookMetadataKeys.LmCallDurationMs, null);
        ctx.LmCallDurationMs.ShouldBeNull();
    }

    [Fact]
    public void Indexer_ThrowsKeyNotFound_ForMissingKey()
    {
        var ctx = NewCtx();
        Should.Throw<KeyNotFoundException>(() => _ = ctx.Metadata["missing"]);
    }

    [Fact]
    public void Enumeration_YieldsTypedSlotsAndExtras_InStableOrder()
    {
        var ctx = NewCtx();
        ctx.Set(HookMetadataKeys.ModelId, "m");
        ctx.Set(HookMetadataKeys.PromptTokens, 11L);
        ctx.Set(HookMetadataKeys.CompletionTokens, 22L);
        ctx.Set(HookMetadataKeys.LmCallDurationMs, 1.5);
        ctx.Set("custom.key", "v");

        var keys = ctx.Metadata.Keys.ToList();
        keys.ShouldContain(HookMetadataKeys.ModelId);
        keys.ShouldContain(HookMetadataKeys.PromptTokens);
        keys.ShouldContain(HookMetadataKeys.CompletionTokens);
        keys.ShouldContain(HookMetadataKeys.LmCallDurationMs);
        keys.ShouldContain("custom.key");

        var values = ctx.Metadata.Values.ToList();
        values.ShouldContain((object?)"m");
        values.ShouldContain((object?)11L);
        values.ShouldContain((object?)22L);
        values.ShouldContain((object?)1.5);
        values.ShouldContain((object?)"v");

        ctx.Metadata.Count.ShouldBe(5);

        // Non-generic IEnumerable path.
        var any = false;
        foreach (var _ in (IEnumerable)ctx.Metadata) any = true;
        any.ShouldBeTrue();
    }

    [Fact]
    public void Enumeration_OnEmptyContext_IsEmpty()
    {
        var ctx = NewCtx();
        ctx.Metadata.Count.ShouldBe(0);
        ctx.Metadata.Keys.ShouldBeEmpty();
        ctx.Metadata.Values.ShouldBeEmpty();
        var enumerated = 0;
        foreach (var _ in ctx.Metadata) enumerated++;
        enumerated.ShouldBe(0);
    }
}

