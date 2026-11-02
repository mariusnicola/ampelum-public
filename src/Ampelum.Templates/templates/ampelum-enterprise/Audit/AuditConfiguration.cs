namespace Company.Enterprise.Audit;

/// <summary>Audit configuration for the enterprise template.</summary>
/// <remarks>
/// Hook this up to your SIEM / logging pipeline. The default redacts
/// Input/Output payloads to protect PII. See docs/THREAT-MODEL.md for the
/// full data-flow contract.
/// </remarks>
public static class AuditConfiguration
{
    public static void Configure(global::Ampelum.Audit.AuditOptions options)
    {
        options.RedactInputs = true;
        options.RedactOutputs = true;
    }
}
