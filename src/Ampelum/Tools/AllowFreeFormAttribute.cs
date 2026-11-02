namespace SignalGrid.Ampelum.Tools;

/// <summary>
/// Assembly-level opt-in marker. Add <c>[assembly: AllowFreeForm]</c> in any assembly that
/// declares <see cref="FreeFormAttribute"/>-tagged tool parameters; without it, AMP1010 fires
/// to surface the (deliberate) increase in prompt-injection surface area. Pre-built
/// <c>Ampelum.Tools.*</c> packages opt in by design.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class AllowFreeFormAttribute : Attribute { }
