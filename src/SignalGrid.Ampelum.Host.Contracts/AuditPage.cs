using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Paginated audit read response from <c>GET /audit</c>. Spec §6.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record AuditPage(
    IReadOnlyList<AuditEntryDto> Entries,
    long? NextSinceSequence);
