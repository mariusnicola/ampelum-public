using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>
/// STJ source-generation context for all wire types in <c>SignalGrid.Ampelum.Host.Contracts</c>.
/// Use <see cref="Default"/> for serialization/deserialization; options are camelCase,
/// <c>WriteIndented=false</c>, <c>DefaultIgnoreCondition=WhenWritingNull</c>.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy           = JsonKnownNamingPolicy.CamelCase,
    WriteIndented                  = false,
    DefaultIgnoreCondition         = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode                 = JsonSourceGenerationMode.Default)]
// Core request/response
[JsonSerializable(typeof(BudgetHint))]
[JsonSerializable(typeof(ForwardRequest))]
[JsonSerializable(typeof(UsageInfo))]
[JsonSerializable(typeof(ForwardResponse))]
// Module discovery
[JsonSerializable(typeof(FieldDescriptor))]
[JsonSerializable(typeof(ModuleDescriptor))]
[JsonSerializable(typeof(ModuleListResponse))]
// Audit
[JsonSerializable(typeof(AuditEntryDto))]
[JsonSerializable(typeof(AuditPage))]
[JsonSerializable(typeof(AuditVerifyResponse))]
// Prompts
[JsonSerializable(typeof(DemoExample))]
[JsonSerializable(typeof(PromptVersion))]
[JsonSerializable(typeof(PromptVersionList))]
[JsonSerializable(typeof(PromptIndexResponse))]
[JsonSerializable(typeof(CreatePromptVersionRequest))]
[JsonSerializable(typeof(PromoteRequest))]
[JsonSerializable(typeof(PromoteResponse))]
// Optimizer
[JsonSerializable(typeof(ExampleSource))]
[JsonSerializable(typeof(InlineExamples))]
[JsonSerializable(typeof(FileExamples))]
[JsonSerializable(typeof(RemoteExamples))]
[JsonSerializable(typeof(OptimizeRequest))]
[JsonSerializable(typeof(OptimizeRunHandle))]
[JsonSerializable(typeof(OptimizeRunStatus))]
// Errors
[JsonSerializable(typeof(ErrorResponse))]
// SSE payloads
[JsonSerializable(typeof(SseTokenPayload))]
[JsonSerializable(typeof(SsePartialPayload))]
[JsonSerializable(typeof(SseFinalPayload))]
[JsonSerializable(typeof(SseErrorPayload))]
[JsonSerializable(typeof(SseDonePayload))]
[JsonSerializable(typeof(SseProgressPayload))]
[JsonSerializable(typeof(SseTrialPayload))]
public partial class AmpelumHostContractsJsonContext : JsonSerializerContext
{
}
