using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Safety.Moderation;

/// <summary>Thrown when <see cref="IOutputModerator"/> flags an output and the moderation hook
/// is operating in fail-closed mode (default).</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class ModerationException : Exception
{
    /// <summary>Builds the exception around the flagged categories.</summary>
    public ModerationException(IReadOnlyList<string> categories)
        : base($"Output moderation flagged categories: {string.Join(",", categories)}.")
    {
        Categories = categories;
    }

    /// <summary>The list of flagged moderation categories.</summary>
    public IReadOnlyList<string> Categories { get; }
}
