using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Hooks;

#pragma warning disable AMPELUM_V09_PREVIEW

namespace SignalGrid.Ampelum.Safety.Moderation;

/// <summary>Hook (Order=300) that moderates parsed outputs after RetryHook (Order=100) but before
/// AuditHook. Throws <see cref="ModerationException"/> on flag unless <c>failOpen=true</c>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class ModerationHook : IHook
{
    /// <summary>Metadata key indicating whether the moderator flagged the outputs.</summary>
    public const string FlaggedKey = "moderation.flagged";

    /// <summary>Metadata key listing the flagged categories (csv).</summary>
    public const string CategoriesKey = "moderation.categories";

    /// <summary>Metadata key written when failOpen=true and the moderator throws.</summary>
    public const string TransportErrorKey = "moderation.transport_error";

    readonly IOutputModerator _moderator;
    readonly bool _failOpen;

    /// <summary>Builds a moderation hook. <paramref name="failOpen"/>=true swallows transport errors.</summary>
    public ModerationHook(IOutputModerator moderator, bool failOpen = false)
    {
        _moderator = moderator;
        _failOpen = failOpen;
    }

    /// <inheritdoc/>
    public int Order => 300;

    /// <inheritdoc/>
    public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct) => default;

    /// <inheritdoc/>
    public async ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
    {
        if (ctx.Outputs is null) return;
        var serialized = ModerationSerializer.SerializeOutputs(ctx.Outputs);
        ModerationResult result;
        try
        {
            result = await _moderator.ModerateAsync(serialized, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (_failOpen)
        {
            ctx.Set(TransportErrorKey, ex.GetType().Name);
            return;
        }
        ctx.Set(FlaggedKey, result.Flagged);
        ctx.Set(CategoriesKey, string.Join(",", result.Categories));
        if (result.Flagged) throw new ModerationException(result.Categories);
    }

    /// <inheritdoc/>
    public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
}
