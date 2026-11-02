using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

#pragma warning disable AMPELUM_V09_PREVIEW

namespace SignalGrid.Ampelum.Safety.Policy;

/// <summary>An immutable list of <see cref="OutputRule"/>s built via <see cref="ContentPolicyBuilder"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class ContentPolicy
{
    /// <summary>The frozen rule list.</summary>
    public IReadOnlyList<OutputRule> Rules { get; }

    internal ContentPolicy(IReadOnlyList<OutputRule> rules) => Rules = rules;

    /// <summary>Empty policy â€” no rules apply.</summary>
    public static ContentPolicy Empty { get; } = new(Array.Empty<OutputRule>());
}
