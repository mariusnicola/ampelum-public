using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Response body of <c>GET /audit/verify</c>. Spec §6.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record AuditVerifyResponse(bool Ok, long? BrokenAt, string Reason);
