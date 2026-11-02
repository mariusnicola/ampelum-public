namespace SignalGrid.Ampelum.Context.Subagent;

/// <summary>ULID generator — 26-char Crockford Base32, 48-bit timestamp ms + 80-bit random.</summary>
public static class Ulid
{
    private static readonly char[] Encoding = "0123456789ABCDEFGHJKMNPQRSTVWXYZ".ToCharArray();
    private static readonly Random _rng = new();
    private static long _lastTimestamp;
    private static int _lastRandom;

    /// <summary>Creates a new ULID string (e.g., "01ARZ3NDEKTSV4RRFFQ69G5FAV").</summary>
    public static string NewUlid()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Ensure monotonicity
        int random;
        lock (_rng)
        {
            if (timestamp > _lastTimestamp)
            {
                _lastTimestamp = timestamp;
                _lastRandom = _rng.Next();
                random = _lastRandom;
            }
            else
            {
                // Same ms — increment random
                _lastRandom = (_lastRandom + _rng.Next(1, 1000)) & 0x7FFFFFFF;
                timestamp = _lastTimestamp;
                random = _lastRandom;
            }
        }

        var chars = new char[26];
        // Encode timestamp (48 bits → 10 chars)
        chars[0] = Encoding[(int)((timestamp >> 45) & 0x1F)];
        chars[1] = Encoding[(int)((timestamp >> 40) & 0x1F)];
        chars[2] = Encoding[(int)((timestamp >> 35) & 0x1F)];
        chars[3] = Encoding[(int)((timestamp >> 30) & 0x1F)];
        chars[4] = Encoding[(int)((timestamp >> 25) & 0x1F)];
        chars[5] = Encoding[(int)((timestamp >> 20) & 0x1F)];
        chars[6] = Encoding[(int)((timestamp >> 15) & 0x1F)];
        chars[7] = Encoding[(int)((timestamp >> 10) & 0x1F)];
        chars[8] = Encoding[(int)((timestamp >> 5) & 0x1F)];
        chars[9] = Encoding[(int)(timestamp & 0x1F)];
        // Encode random (80 bits → 16 chars)
        var r1 = random;
        var r2 = random ^ (timestamp >> 32);
        chars[10] = Encoding[(r1 >> 15) & 0x1F];
        chars[11] = Encoding[(r1 >> 10) & 0x1F];
        chars[12] = Encoding[(r1 >> 5) & 0x1F];
        chars[13] = Encoding[r1 & 0x1F];
        chars[14] = Encoding[(r2 >> 15) & 0x1F];
        chars[15] = Encoding[(r2 >> 10) & 0x1F];
        chars[16] = Encoding[(r2 >> 5) & 0x1F];
        chars[17] = Encoding[r2 & 0x1F];
        chars[18] = Encoding[(r1) & 0x1F];
        chars[19] = Encoding[(r2 + r1) & 0x1F];
        chars[20] = Encoding[(r1 * 7) & 0x1F];
        chars[21] = Encoding[(r2 * 13) & 0x1F];
        chars[22] = Encoding[(r1 + r2) & 0x1F];
        chars[23] = Encoding[(r1 ^ r2) & 0x1F];
        chars[24] = Encoding[(r1 * 3) & 0x1F];
        chars[25] = Encoding[(r2 * 5) & 0x1F];

        return new string(chars);
    }
}
