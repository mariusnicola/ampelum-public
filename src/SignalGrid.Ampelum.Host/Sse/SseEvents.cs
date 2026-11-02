namespace SignalGrid.Ampelum.Host.Sse;

// Host-internal mirror of Contracts.SseEventNames. Avoids cross-assembly reference in hot path.
// Task 4.16 cross-checks string equality.
internal static class SseEvents
{
    public const string Token    = "token";
    public const string Partial  = "partial";
    public const string Final    = "final";
    public const string Error    = "error";
    public const string Done     = "done";
    public const string Progress = "progress";
    public const string Trial    = "trial";
}
