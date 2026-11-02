using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>
/// Wire DTO for a single audit chain entry. Maps from <c>Ampelum.Audit.AuditEntry</c>
/// (file 4 task 4.7). <c>EntryHashHex</c> and <c>PrevHashHex</c> are 64-char lowercase
/// SHA-256 hex strings. <c>Timestamp</c> serialises as ISO 8601 with offset. Spec §6.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record AuditEntryDto(
    long Sequence,
    DateTimeOffset Timestamp,
    string TraceId,
    string ModuleName,
    string EventType,
    string ModelId,
    IReadOnlyDictionary<string, JsonElement> Inputs,
    IReadOnlyDictionary<string, JsonElement> Outputs,
    long PromptTokens,
    long CompletionTokens,
    string EntryHashHex,
    string PrevHashHex,
    int SchemaVersion);
