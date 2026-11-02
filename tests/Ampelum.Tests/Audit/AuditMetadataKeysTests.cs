using SignalGrid.Ampelum.Audit;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Audit;

public sealed class AuditMetadataKeysTests
{
    [Fact]
    public void Existing_Constants_HaveDocumentedStringValues()
    {
        AuditMetadataKeys.ModelId.ShouldBe("SignalGrid.Ampelum.audit.model_id");
        AuditMetadataKeys.PromptTokens.ShouldBe("SignalGrid.Ampelum.audit.prompt_tokens");
        AuditMetadataKeys.CompletionTokens.ShouldBe("SignalGrid.Ampelum.audit.completion_tokens");
    }

    [Fact]
    public void V09g_EventType_Constants_HaveDocumentedStringValues()
    {
        AuditMetadataKeys.EventTypeRouterFailover.ShouldBe("router.failover");
        AuditMetadataKeys.EventTypeSemcacheHit.ShouldBe("semcache.hit");
        AuditMetadataKeys.EventTypeSemcacheStore.ShouldBe("semcache.store");
        AuditMetadataKeys.EventTypeRatelimitAcquired.ShouldBe("ratelimit.acquired");
        AuditMetadataKeys.EventTypeRatelimitRejected.ShouldBe("ratelimit.rejected");
        AuditMetadataKeys.EventTypeCircuitBreakerOpen.ShouldBe("circuitbreaker.open");
        AuditMetadataKeys.EventTypeCircuitBreakerHalfOpen.ShouldBe("circuitbreaker.halfopen");
        AuditMetadataKeys.EventTypeCircuitBreakerClosed.ShouldBe("circuitbreaker.closed");
        AuditMetadataKeys.EventTypeDeadlineExceeded.ShouldBe("deadline.exceeded");
    }

    [Fact]
    public void V09g_Metadata_Keys_HaveDocumentedStringValues()
    {
        AuditMetadataKeys.RouterFrom.ShouldBe("router.from");
        AuditMetadataKeys.RouterTo.ShouldBe("router.to");
        AuditMetadataKeys.RouterErrorKind.ShouldBe("router.error.kind");
    }

    [Fact]
    public void Alpha5_EventType_Discriminator_Constants()
    {
        AuditMetadataKeys.EventType.ShouldBe("SignalGrid.Ampelum.audit.event_type");
        AuditMetadataKeys.EventTypeAgentHandoff.ShouldBe("agent.handoff");
    }

    [Fact]
    public void TraceId_IsStableStringConstant() =>
        AuditMetadataKeys.TraceId.ShouldBe("SignalGrid.Ampelum.audit.trace_id");

    [Fact]
    public void TraceId_FollowsAmpelumAuditNamespaceConvention() =>
        AuditMetadataKeys.TraceId.ShouldStartWith("SignalGrid.Ampelum.audit.");
}
