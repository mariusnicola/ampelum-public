using System.Collections.Generic;
using System.Text;

namespace SignalGrid.Ampelum.Safety.Moderation;

internal static class ModerationSerializer
{
    public static string SerializeOutputs(IReadOnlyDictionary<string, object?> outputs)
    {
        var sb = new StringBuilder();
        foreach (var kv in outputs)
        {
            if (kv.Value is null) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(kv.Key).Append(": ").Append(kv.Value);
        }
        return sb.ToString();
    }
}
