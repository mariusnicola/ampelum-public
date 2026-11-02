using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Hooks;

public sealed class HookMetadataKeysTests
{
    [Fact]
    public void Constants_HaveDocumentedStringValues()
    {
        HookMetadataKeys.ModelId.ShouldBe("lm.model_id");
        HookMetadataKeys.PromptTokens.ShouldBe("lm.tokens.prompt");
        HookMetadataKeys.CompletionTokens.ShouldBe("lm.tokens.completion");
        HookMetadataKeys.LmCallDurationMs.ShouldBe("lm.call.duration_ms");
        HookMetadataKeys.BudgetCostCents.ShouldBe("budget.cost_cents");
        HookMetadataKeys.BudgetScope.ShouldBe("budget.scope");
    }

    [Fact]
    public void V09g_Constants_HaveDocumentedStringValues()
    {
        HookMetadataKeys.RateLimitKey.ShouldBe("rate_limit.key");
        HookMetadataKeys.RouterTargetName.ShouldBe("router.target.name");
        HookMetadataKeys.DeadlineUtc.ShouldBe("deadline.utc");
        HookMetadataKeys.AuditReplaying.ShouldBe("SignalGrid.Ampelum.audit.replaying");
        HookMetadataKeys.SemanticCacheHit.ShouldBe("SignalGrid.Ampelum.semantic_cache_hit");
        HookMetadataKeys.SemanticCacheSimilarity.ShouldBe("SignalGrid.Ampelum.semantic_cache_similarity");
    }

    [Fact]
    public void TraceKeys_HookShortCircuited_HasDocumentedValue()
    {
        TraceKeys.HookShortCircuited.ShouldBe("hook_short_circuited");
    }
}
