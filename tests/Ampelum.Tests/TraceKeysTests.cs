using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

public sealed class TraceKeysTests
{
    [Fact] public void Reasoning_IsStableStringConstant() =>
        TraceKeys.Reasoning.ShouldBe("reasoning");

    [Fact] public void ToolCalls_IsStableStringConstant() =>
        TraceKeys.ToolCalls.ShouldBe("tool_calls");

    [Fact] public void StreamingParseFailure_IsStableStringConstant() =>
        TraceKeys.StreamingParseFailure.ShouldBe("streaming_parse_failure");

    [Fact] public void CacheHit_IsStableStringConstant() =>
        TraceKeys.CacheHit.ShouldBe("lm.cache_hit");

    [Fact] public void RetryAttempt_IsStableStringConstant() =>
        TraceKeys.RetryAttempt.ShouldBe("retry.attempt");
}
