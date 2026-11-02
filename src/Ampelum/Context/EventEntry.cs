using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Context;

/// <summary>Base record for all typed event-log entries. Polymorphic JSON via $type discriminator.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(UserMessage),         "user_msg")]
[JsonDerivedType(typeof(AssistantText),       "assistant")]
[JsonDerivedType(typeof(ToolCall),            "tool_call")]
[JsonDerivedType(typeof(ToolResult),          "tool_result")]
[JsonDerivedType(typeof(ModeTransition),      "mode_transition")]
[JsonDerivedType(typeof(ChapterBoundary),     "chapter_boundary")]
[JsonDerivedType(typeof(Decision),            "decision")]
[JsonDerivedType(typeof(PlanItem),            "plan_item")]
[JsonDerivedType(typeof(SubagentInvocation),  "subagent")]
[JsonDerivedType(typeof(SummaryEntry),        "summary")]
[JsonDerivedType(typeof(SanitizationSummary), "sanitization_summary")]
[JsonDerivedType(typeof(CostEvent), "cost")]
public abstract record EventEntry(
    int Turn,
    int Seq,
    DateTimeOffset Timestamp,
    string Key,
    string? SupersededBy);

/// <summary>A user-authored message that starts a turn.</summary>
public sealed record UserMessage(int Turn, int Seq, DateTimeOffset Timestamp, string Key, string? SupersededBy, string Text) : EventEntry(Turn, Seq, Timestamp, Key, SupersededBy);

/// <summary>Textual response from the assistant (model).</summary>
public sealed record AssistantText(int Turn, int Seq, DateTimeOffset Timestamp, string Key, string? SupersededBy, string Text) : EventEntry(Turn, Seq, Timestamp, Key, SupersededBy);

/// <summary>A tool invocation made by the assistant.</summary>
public sealed record ToolCall(int Turn, int Seq, DateTimeOffset Timestamp, string Key, string? SupersededBy, string Name, string ArgsJson) : EventEntry(Turn, Seq, Timestamp, Key, SupersededBy);

/// <summary>Result of a tool execution, including content hash for dedup.</summary>
public sealed record ToolResult(
    int Turn, int Seq, DateTimeOffset Timestamp, string Key, string? SupersededBy,
    string ToolCallKey, string Body, int Bytes, string ContentHashHex,
    string? Path, DateTimeOffset? FileMtime) : EventEntry(Turn, Seq, Timestamp, Key, SupersededBy);

/// <summary>Log of a mode transition (e.g., /build → /talk) with carried artifacts.</summary>
public sealed record ModeTransition(
    int Turn, int Seq, DateTimeOffset Timestamp, string Key, string? SupersededBy,
    string From, string To, IReadOnlyList<CarriedArtifact> CarriedArtifacts) : EventEntry(Turn, Seq, Timestamp, Key, SupersededBy);

/// <summary>Marker event when a chapter closes.</summary>
public sealed record ChapterBoundary(int Turn, int Seq, DateTimeOffset Timestamp, string Key, string? SupersededBy, string ChapterId, string Reason) : EventEntry(Turn, Seq, Timestamp, Key, SupersededBy);

/// <summary>A key decision made during the session with source event references.</summary>
public sealed record Decision(int Turn, int Seq, DateTimeOffset Timestamp, string Key, string? SupersededBy, string Statement, IReadOnlyList<string> SourceEventKeys) : EventEntry(Turn, Seq, Timestamp, Key, SupersededBy);

/// <summary>A tracked work item within a session.</summary>
public sealed record PlanItem(int Turn, int Seq, DateTimeOffset Timestamp, string Key, string? SupersededBy, string Id, string Title, string Status) : EventEntry(Turn, Seq, Timestamp, Key, SupersededBy);

/// <summary>Record of a subagent invocation, including its result and log path.</summary>
public sealed record SubagentInvocation(int Turn, int Seq, DateTimeOffset Timestamp, string Key, string? SupersededBy, string SubagentJti, string TaskId, string ResultJson, string ArchivedLogPath) : EventEntry(Turn, Seq, Timestamp, Key, SupersededBy);

/// <summary>Summarised content of a closed chapter, with embeddings and key decisions.</summary>
public sealed record SummaryEntry(
    int Turn, int Seq, DateTimeOffset Timestamp, string Key, string? SupersededBy,
    string ChapterId, IReadOnlyList<string> CoversKeys,
    string Title, string Abstract,
    IReadOnlyList<GroundedDecision> KeyDecisions,
    IReadOnlyList<string> Artifacts, IReadOnlyList<string> References,
    int OriginalTokens, int SummaryTokens,
    string EmbedderVersion,
    byte[] AbstractEmbeddingBlob) : EventEntry(Turn, Seq, Timestamp, Key, SupersededBy);

/// <summary>Per-turn aggregate of sanitization activity (redactions and kind counts).</summary>
public sealed record SanitizationSummary(
    int Turn, int Seq, DateTimeOffset Timestamp, string Key, string? SupersededBy,
    int TotalRedactions, IReadOnlyDictionary<string, int> KindCounts) : EventEntry(Turn, Seq, Timestamp, Key, SupersededBy);

/// <summary>Cost event for a turn.</summary>
public sealed record CostEvent(
    int Turn, int Seq, DateTimeOffset Timestamp, string Key, string? SupersededBy,
    int InputTokens, int OutputTokens, int CacheReadTokens, int CacheWriteTokens,
    decimal InputCost, decimal OutputCost, decimal CacheReadCost, decimal CacheWriteCost, decimal TotalCost)
    : EventEntry(Turn, Seq, Timestamp, Key, SupersededBy);