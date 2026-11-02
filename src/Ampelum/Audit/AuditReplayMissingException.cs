namespace SignalGrid.Ampelum.Audit;

/// <summary>
/// Thrown by <see cref="AuditHook"/> in replay mode when no entry matches the lookup key
/// <c>(ModuleName, hash(canonical Inputs))</c>. Spec Â§4.3; PSQ-5.
/// </summary>
public sealed class AuditReplayMissingException : InvalidOperationException
{
    /// <summary>Name of the module whose replay lookup failed.</summary>
    public string ModuleName { get; }

    /// <summary>Initializes a new instance describing the missing replay key.</summary>
    /// <param name="moduleName">Module name that produced no match in the recorded sink.</param>
    public AuditReplayMissingException(string moduleName)
        : base($"Audit replay missing: no entry matches module '{moduleName}' and the supplied inputs.")
    {
        ModuleName = moduleName;
    }
}
