using System.Text.Json.Serialization;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Endpoints;
using SignalGrid.Ampelum.Host.Persistence;

namespace SignalGrid.Ampelum.Host.Internal;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ForwardRequest))]
[JsonSerializable(typeof(ForwardResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ModuleListResponse))]
[JsonSerializable(typeof(ModuleDescriptor))]
[JsonSerializable(typeof(AuditPage))]
[JsonSerializable(typeof(AuditEntryDto))]
[JsonSerializable(typeof(AuditVerifyResponse))]
[JsonSerializable(typeof(PromptIndexResponse))]
[JsonSerializable(typeof(PromptVersionList))]
[JsonSerializable(typeof(PromptVersion))]
[JsonSerializable(typeof(CreatePromptVersionRequest))]
[JsonSerializable(typeof(PromoteRequest))]
[JsonSerializable(typeof(PromoteResponse))]
[JsonSerializable(typeof(OptimizeRequest))]
[JsonSerializable(typeof(OptimizeRunHandle))]
[JsonSerializable(typeof(OptimizeRunStatus))]
[JsonSerializable(typeof(SseTokenPayload))]
[JsonSerializable(typeof(SsePartialPayload))]
[JsonSerializable(typeof(SseFinalPayload))]
[JsonSerializable(typeof(SseErrorPayload))]
[JsonSerializable(typeof(SseDonePayload))]
[JsonSerializable(typeof(SseProgressPayload))]
[JsonSerializable(typeof(SseTrialPayload))]
[JsonSerializable(typeof(RebuildSummary))]
[JsonSerializable(typeof(ReadyResponse))]
// Host-internal types (persistence rows + file shapes + SSE carrier)
[JsonSerializable(typeof(AuditIndexRow))]
[JsonSerializable(typeof(PromptVersionRow))]
[JsonSerializable(typeof(OptimizerRunRow))]
[JsonSerializable(typeof(ModuleSnapshotRow))]
[JsonSerializable(typeof(SseEnvelope))]
[JsonSerializable(typeof(PromptVersionFileShape))]
[JsonSerializable(typeof(PromptMeta))]
[JsonSerializable(typeof(DemoExampleShape))]
internal sealed partial class AmpelumHostJsonContext : JsonSerializerContext;
