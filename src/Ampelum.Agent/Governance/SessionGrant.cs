namespace SignalGrid.Ampelum.Agent;

/// <summary>Session-scoped permission grant that can promote a tool's policy decision.
/// Grants are issued by human operators in response to escalations or by workflow
/// configuration for pre-approved operations. Each grant is single-tool and time-limited.
/// Grants cannot bypass hard Catastrophic blocks.
/// All grants are audited at issuance time.</summary>
public sealed record SessionGrant(
    string GrantId,
    string CallerInstanceId,
    string ToolName,
    PolicyDecision GrantedDecision,
    DateTimeOffset ExpiresAt,
    string Reason
)
{
    /// <summary>Check if this grant has expired.</summary>
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
}
