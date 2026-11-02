using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Safety.Policy;

/// <summary>Action taken by <see cref="ContentPolicyHook"/> when an <see cref="OutputRule"/> matches.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public enum OutputRuleAction
{
    /// <summary>Throw <see cref="ContentPolicyViolationException"/>.</summary>
    Reject,
    /// <summary>Replace the matched substring with the rule's redaction token.</summary>
    Redact,
    /// <summary>Set warning metadata on <c>HookContext</c> and continue.</summary>
    Warn,
}
