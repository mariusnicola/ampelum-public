using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Caching.Semantic;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Tests.Caching.Semantic;

/// <summary>The audit chain next to the semantic cache, through the real Predict.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class SemanticCacheAuditTests
{
    [Signature("Answer the question.")]
    public sealed record Ask : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    static string Reply(string a) => "[[ ## Answer ## ]]\n" + a + "\n[[ ## completed ## ]]";

    static async Task<List<AuditEntry>> EntriesAsync(InMemoryAuditSink sink)
    {
        var list = new List<AuditEntry>();
        await foreach (var e in sink.EnumerateAsync(TestContext.Current.CancellationToken)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task A_miss_records_the_producing_model_and_a_hit_records_that_model_with_zero_tokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = new InMemoryAuditSink();
        var chat = new FakeChatClient().EnqueueText(
            Reply("yes"), new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }, "model-x", null);
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
            .WithHook(new AuditHook(sink))
            .WithHook(new SemanticCacheHook(new InMemorySemanticCache()));
        var predict = new Predict<Ask>(ctx);

        await predict.ForwardAsync(new Ask { Question = "Do you ship?" }, ct: ct);
        var hit = await predict.ForwardAsync(new Ask { Question = "Do you ship?" }, ct: ct);

        chat.RecordedCalls.Count.ShouldBe(1);
        hit.Value.Answer.ShouldBe("yes");
        var entries = await EntriesAsync(sink);
        entries.Count.ShouldBe(2);
        entries[0].ModelId.ShouldBe("model-x");
        entries[0].PromptTokens.ShouldBe(10);
        entries[0].CompletionTokens.ShouldBe(5);
        entries[1].ModelId.ShouldBe("model-x");
        entries[1].PromptTokens.ShouldBe(0);
        entries[1].CompletionTokens.ShouldBe(0);
    }

    [Fact]
    public async Task An_entry_stored_with_only_the_hook_metadata_model_id_is_read_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = new InMemorySemanticCache();
        var hook = new SemanticCacheHook(cache);
        var miss = new SignalGrid.Ampelum.Hooks.HookContext
        {
            ModuleName = "Predict",
            Inputs = new Dictionary<string, object?> { ["Q"] = "q" },
            Outputs = new Dictionary<string, object?> { ["A"] = "a" },
        };
        miss.Set(SignalGrid.Ampelum.HookMetadataKeys.ModelId, "only-hook-key");

        await hook.OnAfterAsync(miss, ct);

        cache.TryGet(SemanticCacheKey.Compute("Predict", miss.Inputs))!.ModelId.ShouldBe("only-hook-key");
    }
}
