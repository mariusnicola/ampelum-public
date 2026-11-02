using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Hooks;

namespace SignalGrid.Ampelum.Safety.PromptInjection;

/// <summary>Hook (Order=75) that scans every string-typed input for prompt-injection patterns.
/// Default behaviour: throw <see cref="PromptInjectionDetectedException"/> on flag. Subclass
/// and override <see cref="OnFlaggedAsync"/> to log + continue instead.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public class PromptInjectionHook : IHook
{
    /// <summary>Metadata key set when an input was flagged.</summary>
    public const string FlaggedKey = "prompt_injection.flagged";

    /// <summary>Metadata key carrying the matched rule identifier.</summary>
    public const string RuleIdKey = "prompt_injection.rule_id";

    readonly IPromptInjectionDetector _detector;

    /// <summary>Builds the hook around the given detector.</summary>
    public PromptInjectionHook(IPromptInjectionDetector detector) => _detector = detector;

    /// <inheritdoc/>
    public int Order => 75;

    /// <inheritdoc/>
    public async ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
    {
        foreach (var kv in ctx.Inputs)
        {
            if (kv.Value is not string s) continue;
            var verdict = _detector.Scan(s);
            if (!verdict.Flagged) continue;
            await OnFlaggedAsync(ctx, verdict, ct).ConfigureAwait(false);
            break;
        }
    }

    /// <summary>Default flag handler â€” throws. Override for log-and-continue semantics.</summary>
    protected virtual ValueTask OnFlaggedAsync(HookContext ctx, PromptInjectionResult verdict, CancellationToken ct)
    {
        ctx.Set(FlaggedKey, true);
        ctx.Set(RuleIdKey, verdict.RuleId);
        throw new PromptInjectionDetectedException(verdict.RuleId!, verdict.Excerpt);
    }

    /// <inheritdoc/>
    public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) => default;

    /// <inheritdoc/>
    public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
}
