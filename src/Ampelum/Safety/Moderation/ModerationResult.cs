using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Safety.Moderation;

/// <summary>The verdict from a single <see cref="IOutputModerator.ModerateAsync"/> call.</summary>
/// <param name="Flagged">True when at least one moderation category was triggered.</param>
/// <param name="Categories">The triggered category identifiers (provider-specific names).</param>
/// <param name="Confidence">Optional confidence/severity in the [0,1] range when supplied by the provider.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record ModerationResult(
    bool Flagged,
    IReadOnlyList<string> Categories,
    double? Confidence);
