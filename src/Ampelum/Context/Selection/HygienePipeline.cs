using System;
using System.Collections.Generic;
using SignalGrid.Ampelum.Context;

namespace SignalGrid.Ampelum.Context.Selection;

/// <summary>Pipeline for deduplication and aging of tool results before selection.</summary>
public sealed class HygienePipeline
{
    private readonly HashSet<string> _seenContentHashes = new();
    private readonly Dictionary<(string Path, DateTimeOffset Mtime), string> _pathCache = new();

    /// <summary>Deduplicates tool results by content hash within the session.</summary>
    public IEnumerable<EventEntry> Deduplicate(IEnumerable<EventEntry> events)
    {
        foreach (var e in events)
        {
            if (e is ToolResult tr)
            {
                if (tr.ContentHashHex is not null && !_seenContentHashes.Add(tr.ContentHashHex))
                {
                    yield return tr with
                    {
                        Body = $"[tool:{tr.ToolCallKey} — duplicate content; body available at turn {e.Turn}]"
                    };
                    continue;
                }
                if (tr.Path is not null && tr.FileMtime is not null)
                {
                    var key = (tr.Path, tr.FileMtime.Value);
                    if (_pathCache.TryGetValue(key, out var existingBody))
                    {
                        yield return tr with { Body = $"[tool:{tr.ToolCallKey} — file previously read; body available at turn {e.Turn}]" };
                        continue;
                    }
                    _pathCache[key] = tr.Body;
                }
            }
            yield return e;
        }
    }

    /// <summary>Ages out tool results older than N turns.</summary>
    public static IEnumerable<EventEntry> AgeToolResults(IEnumerable<EventEntry> events, int currentTurn, int maxAgeTurns)
    {
        foreach (var e in events)
        {
            if (e is ToolResult tr && e.Turn < currentTurn - maxAgeTurns)
            {
                yield return tr with
                {
                    Body = $"[{tr.ToolCallKey} — body aged out at turn {e.Turn}; re-read if needed]"
                };
            }
            else
            {
                yield return e;
            }
        }
    }
}
