using SignalGrid.Ampelum;
using SignalGrid.Ampelum.ProviderAdapters;
using Shouldly;
using Xunit;

namespace Ampelum.IntegrationTests.Pareto;

/// <summary>
/// Pareto integration test #15 (user-overrode plan §6.H mapping for v0.9.1-alpha.3).
/// Provider format adapter round-trip: <see cref="ToolCallFormatConverter"/> moves a
/// realistic tool-call payload — nested arguments + parallel calls — across
/// OpenAI → Anthropic → OpenAI without losing data.
/// </summary>
/// <remarks>
/// This replaces the original v0.9h plan #15 ("Optimizer mid-trial failure"), which is
/// deferred. The converter is pure JSON and needs no fixture or real LM, so this test
/// runs unconditionally (no env-var skip). Equality is by tool name + argument bag —
/// the OpenAI shape carries native ids, so we additionally assert ids round-trip when
/// the source format provides them.
/// </remarks>
public sealed class Pareto15_ProviderFormatRoundTrip
{
    /// <summary>OAI → Anthropic → OAI preserves id, name, and nested + parallel argument bags.</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public void OpenAi_Anthropic_OpenAi_Roundtrip_Preserves_AllToolCallData()
    {
        const string oaiJson = """
            [
              {
                "id": "call_abc123",
                "type": "function",
                "function": {
                  "name": "search_orders",
                  "arguments": "{\"customer\":{\"id\":42,\"vip\":true},\"filters\":[\"open\",\"recent\"],\"limit\":5}"
                }
              },
              {
                "id": "call_def456",
                "type": "function",
                "function": {
                  "name": "fetch_invoice",
                  "arguments": "{\"invoice\":{\"number\":\"INV-7\",\"meta\":{\"region\":\"EU\",\"total\":129.5}}}"
                }
              }
            ]
            """;

        // Step 1: OAI -> normalized.
        var fromOai = ToolCallFormatConverter.FromOpenAi(oaiJson);
        fromOai.Count.ShouldBe(2);
        fromOai[0].Id.ShouldBe("call_abc123");
        fromOai[0].Name.ShouldBe("search_orders");
        fromOai[1].Id.ShouldBe("call_def456");
        fromOai[1].Name.ShouldBe("fetch_invoice");

        // Step 2: normalized -> Anthropic.
        var anthropicJson = ToolCallFormatConverter.ToAnthropic(fromOai);
        anthropicJson.ShouldContain("\"type\":\"tool_use\"");
        anthropicJson.ShouldContain("\"name\":\"search_orders\"");
        anthropicJson.ShouldContain("\"name\":\"fetch_invoice\"");

        // Step 3: Anthropic -> normalized (round-trip half).
        var fromAnthropic = ToolCallFormatConverter.FromAnthropic(anthropicJson);
        fromAnthropic.Count.ShouldBe(2);

        // Step 4: normalized -> OAI again.
        var oaiJson2 = ToolCallFormatConverter.ToOpenAi(fromAnthropic);
        var fromOai2 = ToolCallFormatConverter.FromOpenAi(oaiJson2);

        // Final equality: ids preserved (Anthropic carries native ids), names preserved,
        // arguments deeply equal — nested objects + arrays + scalars all round-trip.
        fromOai2.Count.ShouldBe(2);
        for (var i = 0; i < fromOai2.Count; i++)
        {
            fromOai2[i].Id.ShouldBe(fromOai[i].Id);
            fromOai2[i].Name.ShouldBe(fromOai[i].Name);
            ArgsEqual(fromOai2[i].Arguments, fromOai[i].Arguments).ShouldBeTrue(
                $"call {i} ({fromOai[i].Name}) arguments diverged after OAI->Anthropic->OAI round-trip");
        }

        // Spot-check on parallel-call data: both calls survived, no merging or dropping.
        fromOai2.Select(c => c.Name).ToArray().ShouldBe(new[] { "search_orders", "fetch_invoice" });

        // Spot-check on nested data: customer.vip survived as bool, filters survived as array.
        var search = fromOai2[0].Arguments;
        search["customer"].ShouldBeOfType<Dictionary<string, object?>>();
        var customer = (IReadOnlyDictionary<string, object?>)search["customer"]!;
        customer["vip"].ShouldBe(true);
        customer["id"].ShouldBe(42L);
        var filters = (IReadOnlyList<object?>)search["filters"]!;
        filters.Count.ShouldBe(2);
        filters[0].ShouldBe("open");
        filters[1].ShouldBe("recent");
        search["limit"].ShouldBe(5L);
    }

    /// <summary>Empty argument bags round-trip cleanly (regression for Anthropic missing <c>input</c>).</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public void OpenAi_Anthropic_OpenAi_Roundtrip_EmptyArgs_Preserved()
    {
        const string oaiJson = """
            [{"id":"call_x","type":"function","function":{"name":"now","arguments":"{}"}}]
            """;
        var fromOai = ToolCallFormatConverter.FromOpenAi(oaiJson);
        var anthropic = ToolCallFormatConverter.ToAnthropic(fromOai);
        var fromAnthropic = ToolCallFormatConverter.FromAnthropic(anthropic);
        var oai2 = ToolCallFormatConverter.ToOpenAi(fromAnthropic);
        var fromOai2 = ToolCallFormatConverter.FromOpenAi(oai2);

        fromOai2.Count.ShouldBe(1);
        fromOai2[0].Id.ShouldBe("call_x");
        fromOai2[0].Name.ShouldBe("now");
        fromOai2[0].Arguments.Count.ShouldBe(0);
    }

    static bool ArgsEqual(IReadOnlyDictionary<string, object?> a, IReadOnlyDictionary<string, object?> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var bv)) return false;
            if (!ValueEqual(kv.Value, bv)) return false;
        }
        return true;
    }

    static bool ValueEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a is IReadOnlyDictionary<string, object?> ad && b is IReadOnlyDictionary<string, object?> bd)
            return ArgsEqual(ad, bd);
        if (a is IReadOnlyList<object?> al && b is IReadOnlyList<object?> bl)
        {
            if (al.Count != bl.Count) return false;
            for (var i = 0; i < al.Count; i++)
                if (!ValueEqual(al[i], bl[i])) return false;
            return true;
        }
        return Equals(a, b);
    }
}
