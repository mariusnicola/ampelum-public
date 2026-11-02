using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>
/// Body of <c>POST /modules/{moduleName}/forward</c> and <c>/stream</c>. Spec §5/§6.
/// </summary>
/// <param name="Inputs">Signature-input field map; values are wire-typed JsonElements.</param>
/// <param name="PromptVersionId">Optional explicit prompt-version override. Null = active version for module.</param>
/// <param name="ParentTraceId">Optional W3C-format parent trace id (32 lowercase hex).</param>
/// <param name="Budget">Optional per-call budget cap.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record ForwardRequest(
    IReadOnlyDictionary<string, JsonElement> Inputs,
    string? PromptVersionId = null,
    string? ParentTraceId = null,
    BudgetHint? Budget = null);
