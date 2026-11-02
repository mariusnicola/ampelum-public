namespace SignalGrid.Ampelum.Context.Chapters;

/// <summary>Reason why a chapter was closed.</summary>
public enum ChapterCloseReason
{
    /// <summary>User turn count exceeded the threshold.</summary>
    UserTurnLimit,
    /// <summary>Total event count exceeded the threshold.</summary>
    EventCountLimit,
    /// <summary>Semantic distance between events exceeded threshold.</summary>
    SemanticDistance,
    /// <summary>A mode transition occurred (e.g., /talk -> /build).</summary>
    ModeTransition,
    /// <summary>Idle timeout elapsed without user activity.</summary>
    IdleTimeout,
    /// <summary>Explicit user request via /chapter command.</summary>
    Explicit
}
