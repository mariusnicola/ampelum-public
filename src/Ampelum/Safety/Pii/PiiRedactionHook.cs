using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Hooks;

namespace SignalGrid.Ampelum.Safety.Pii;

/// <summary>
/// Hook (Order=50) that redacts PII from <see cref="HookContext.Inputs"/> on OnBefore and
/// <see cref="HookContext.Outputs"/> on OnAfter using an injected <see cref="IPiiRedactor"/>.
/// Sets <c>"pii.redacted" = true</c> in metadata when any value was rewritten.
///
/// <para>Audit-pipeline contract: this hook runs before <see cref="Ampelum.Audit.AuditHook"/>,
/// so the audit chain hash commits the redacted form. Restoring plaintext after-the-fact
/// invalidates the chain â€” see <c>docs/security/threat-model-v1.md</c> Â§6.</para>
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class PiiRedactionHook : IHook
{
    /// <summary>Metadata key under which the redacted Inputs projection is published.</summary>
    public const string RedactedInputsKey = "pii.redacted_inputs";

    /// <summary>Metadata key set when any redaction occurred.</summary>
    public const string RedactedFlagKey = "pii.redacted";

    readonly IPiiRedactor _redactor;

    /// <summary>Builds a redaction hook around the given redactor.</summary>
    public PiiRedactionHook(IPiiRedactor redactor) => _redactor = redactor;

    /// <inheritdoc/>
    public int Order => 50;

    /// <inheritdoc/>
    public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
    {
        var redacted = _redactor.RedactDictionary(ctx.Inputs);
        ctx.Set(RedactedInputsKey, redacted);
        ctx.Set(RedactedFlagKey, true);
        return default;
    }

    /// <inheritdoc/>
    public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
    {
        if (ctx.Outputs is null) return default;
        ctx.Outputs = _redactor.RedactDictionary(ctx.Outputs);
        ctx.Set(RedactedFlagKey, true);
        return default;
    }

    /// <inheritdoc/>
    public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
}
