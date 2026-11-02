using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Caching.Semantic;

/// <summary>
/// Cached payload stored by <see cref="ISemanticCache"/>. Captures the parsed module outputs
/// together with the audit-determinism metadata needed so that a cache-hit forward pass can
/// reproduce the same audit chain entry as the original cache-miss call (PSQ-2 F4 guard).
/// </summary>
/// <param name="Outputs">Parsed module outputs (key/value field projection).</param>
/// <param name="ModelId">Model id of the producing call. Required for audit chain hashing.</param>
/// <param name="PromptTokens">Prompt token count of the producing call (mirrors <c>HookMetadataKeys.PromptTokens</c>).</param>
/// <param name="CompletionTokens">Completion token count of the producing call.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record SemanticCacheEntry(
    IReadOnlyDictionary<string, object?> Outputs,
    string ModelId,
    long PromptTokens,
    long CompletionTokens);
