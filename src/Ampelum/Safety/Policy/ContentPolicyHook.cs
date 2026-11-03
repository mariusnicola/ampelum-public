using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Hooks;

#pragma warning disable AMPELUM_V09_PREVIEW

namespace SignalGrid.Ampelum.Safety.Policy;

/// <summary>Hook (Order=250) that evaluates a <see cref="ContentPolicy"/> against parsed outputs.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class ContentPolicyHook : IHook
{
    /// <summary>Metadata key set when at least one Warn rule matched.</summary>
    public const string WarnedKey = "policy.warned";

    /// <summary>Metadata key listing the matched Warn rule ids (csv).</summary>
    public const string WarnedRulesKey = "policy.warned.rules";

    readonly ContentPolicy _policy;

    /// <summary>Builds the hook around an immutable policy snapshot.</summary>
    public ContentPolicyHook(ContentPolicy policy) => _policy = policy;

    /// <inheritdoc/>
    public int Order => 250;

    /// <inheritdoc/>
    public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct) => default;

    /// <inheritdoc/>
    public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
    {
        if (ctx.Outputs is null || _policy.Rules.Count == 0) return default;

        var rewrite = new Dictionary<string, object?>(ctx.Outputs.Count, StringComparer.Ordinal);
        var warned = new List<string>();
        bool mutated = false;

        foreach (var kv in ctx.Outputs)
        {
            if (kv.Value is not string s) { rewrite[kv.Key] = kv.Value; continue; }
            string current = s;
            foreach (var rule in _policy.Rules)
            {
                var match = rule.Pattern.Match(current);
                if (!match.Success) continue;
                switch (rule.OnMatch)
                {
                    case OutputRuleAction.Reject:
                        // The after hooks all run even when this one throws; the rejected text must not reach them.
                        ctx.Outputs = new Dictionary<string, object?>();
                        throw new ContentPolicyViolationException(rule.RuleId, match.Value);
                    case OutputRuleAction.Redact:
                        current = rule.Pattern.Replace(current, rule.RedactionToken ?? "<REDACTED>");
                        mutated = true;
                        break;
                    case OutputRuleAction.Warn:
                        warned.Add(rule.RuleId);
                        break;
                }
            }
            rewrite[kv.Key] = current;
        }

        if (mutated) ctx.Outputs = rewrite;
        if (warned.Count > 0)
        {
            ctx.Set(WarnedKey, true);
            ctx.Set(WarnedRulesKey, string.Join(",", warned));
        }
        return default;
    }

    /// <inheritdoc/>
    public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
}
