namespace SignalGrid.Ampelum.Context;

/// <summary>
/// Generator for event keys of the form type:turn:seq:hash-prefix (e.g., "user_msg:1:0:abc12345").
/// </summary>
public static class EventKey
{
    /// <summary>Creates a deterministic event key from type, turn, seq, and payload content.</summary>
    public static string Make(string type, int turn, int seq, string payload)
    {
        var hashFull = ContentHasher.HashHex(payload);
        return $"{type}:{turn}:{seq}:{hashFull[..8]}";
    }
}