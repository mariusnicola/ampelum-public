namespace SignalGrid.Ampelum.Repl;

/// <summary>
/// Globals object exposed to every REPL script. Holds the active <see cref="AmpelumContext"/>;
/// scripts can reference it as <c>Ctx</c>.
/// </summary>
public sealed class ReplGlobals
{
    /// <summary>The Ampelum context shared across REPL evaluations.</summary>
    public AmpelumContext Ctx { get; }

    /// <summary>Creates a new globals instance bound to the given context.</summary>
    /// <param name="ctx">Active Ampelum context.</param>
    public ReplGlobals(AmpelumContext ctx) => Ctx = ctx;
}
