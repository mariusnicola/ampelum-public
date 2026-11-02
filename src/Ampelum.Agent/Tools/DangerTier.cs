namespace SignalGrid.Ampelum.Agent;

/// <summary>Five-tier danger classification for agent tools.
/// Default policy: Safe=AutoApprove, Additive=AutoApprove, Mutating=SessionApprove,
/// Destructive=RequiresConfirmation, Catastrophic=Block.
/// See ampelum-tool-manifest spec for policy matrix.</summary>
public enum DangerTier
{
    /// <summary>Read-only, no side effects. E.g., read_file, list_directory, http_get. Policy: AutoApprove.</summary>
    Safe = 0,

    /// <summary>Creates new state, no mutation to existing resources. E.g., create_file, send_email (new thread). Policy: AutoApprove.</summary>
    Additive = 1,

    /// <summary>Modifies existing state. E.g., edit_file, update_record. Policy: SessionApprove (once per session).</summary>
    Mutating = 2,

    /// <summary>Deletes or irreversibly changes state. E.g., delete_file, drop_table. Policy: RequiresConfirmation.</summary>
    Destructive = 3,

    /// <summary>Full-system or mass deletion. E.g., rm -rf, mass delete. Policy: Block (no override).</summary>
    Catastrophic = 4
}

/// <summary>Reversibility of a tool's effects. Used by PolicyEngine to decide confirmation gates.
/// Orthogonal to DangerTier: PolicyEngine combines both to compute final policy.</summary>
public enum Reversibility
{
    /// <summary>Undo is immediate and lossless. E.g., in-memory state change.</summary>
    Reversible = 0,

    /// <summary>Undo is possible but requires a step. E.g., git revert, delete-and-restore from backup.</summary>
    DurableWithUndo = 1,

    /// <summary>Undo is not possible. E.g., sent email, destroyed secret, external API call.</summary>
    Irreversible = 2
}