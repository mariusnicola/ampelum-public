using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Safety.Pii;

/// <summary>Coarse-grained PII category classification used by <see cref="PiiRule"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public enum PiiCategory
{
    /// <summary>Email address.</summary>
    Email,
    /// <summary>Phone number.</summary>
    Phone,
    /// <summary>US Social Security Number.</summary>
    Ssn,
    /// <summary>Credit-card number.</summary>
    CreditCard,
    /// <summary>Other / extension category.</summary>
    Other,
}
