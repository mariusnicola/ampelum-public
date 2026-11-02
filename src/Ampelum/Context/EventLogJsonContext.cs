using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Context;

/// <summary>Source-generated JSON serializer context for the closed EventEntry and CarriedArtifact taxonomies.</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(EventEntry))]
[JsonSerializable(typeof(UserMessage))]
[JsonSerializable(typeof(AssistantText))]
[JsonSerializable(typeof(ToolCall))]
[JsonSerializable(typeof(ToolResult))]
[JsonSerializable(typeof(ModeTransition))]
[JsonSerializable(typeof(ChapterBoundary))]
[JsonSerializable(typeof(Decision))]
[JsonSerializable(typeof(PlanItem))]
[JsonSerializable(typeof(SubagentInvocation))]
[JsonSerializable(typeof(SummaryEntry))]
[JsonSerializable(typeof(SanitizationSummary))]
[JsonSerializable(typeof(CostEvent))]
[JsonSerializable(typeof(CarriedArtifact))]
[JsonSerializable(typeof(CarriedPlan))]
[JsonSerializable(typeof(PlanItemSnapshot))]
[JsonSerializable(typeof(CarriedDecisions))]
[JsonSerializable(typeof(CarriedFiles))]
[JsonSerializable(typeof(CarriedTestResults))]
[JsonSerializable(typeof(TestResultSummary))]
internal partial class EventLogJsonContext : JsonSerializerContext { }
