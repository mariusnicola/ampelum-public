namespace SignalGrid.Ampelum.Tools;

/// <summary>
/// Marks a string property or parameter as intentionally free-form. Spec Â§3.2.
/// Without this attribute, AMP001 fires.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = false)]
public sealed class FreeFormAttribute : Attribute { }
