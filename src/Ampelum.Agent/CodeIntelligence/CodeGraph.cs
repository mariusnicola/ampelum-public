namespace SignalGrid.Ampelum.Agent.CodeIntelligence;

/// <summary>
/// Graph builder + query API backed by IndexStore's SQLite code_edges table.
/// Provides call-graph and dependency-graph queries — "what calls X" and "what depends on X".
/// </summary>
public sealed class CodeGraph
{
    private readonly IndexStore _store;

    public CodeGraph(IndexStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Who calls this symbol? Returns edges where this symbol is the target of a Call edge.</summary>
    public CodeGraphEdge[] GetCallers(string symbolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolName);
        return _store.GetEdgesByTgt(symbolName)
            .Where(static e => e.EdgeType == EdgeType.Call)
            .ToArray();
    }

    /// <summary>What does this symbol call? Returns edges where this symbol is the source of a Call edge.</summary>
    public CodeGraphEdge[] GetCallees(string symbolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolName);
        return _store.GetEdgesBySrc(symbolName)
            .Where(static e => e.EdgeType == EdgeType.Call)
            .ToArray();
    }

    /// <summary>
    /// What depends on this symbol? Returns edges where this symbol is the target
    /// and the edge type indicates a structural dependency (Extends, Implements, or References).
    /// </summary>
    public CodeGraphEdge[] GetDependents(string symbolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolName);
        return _store.GetEdgesByTgt(symbolName)
            .Where(static e => e.EdgeType is EdgeType.Extends or EdgeType.Implements or EdgeType.References)
            .ToArray();
    }

    /// <summary>
    /// Find the shortest path between two symbols using BFS over the code_edges table.
    /// Returns the sequence of edges forming the path, or an empty array if no path exists.
    /// </summary>
    public CodeGraphEdge[] FindPath(string from, string to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);

        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return [];

        // BFS: visit nodes by symbol name, track the edge used to reach each node
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { from };
        var queue = new Queue<string>();
        queue.Enqueue(from);

        // predecessor map: nodeName -> (predecessorNodeName, edgeThatLedToNode)
        var predecessor = new Dictionary<string, (string Prev, CodeGraphEdge Edge)>(StringComparer.OrdinalIgnoreCase);

        bool found = false;

        while (queue.Count > 0 && !found)
        {
            var current = queue.Dequeue();
            var edges = _store.GetEdgesBySrc(current);

            foreach (var edge in edges)
            {
                var neighbor = edge.TgtName;
                if (visited.Add(neighbor))
                {
                    predecessor[neighbor] = (current, edge);
                    if (string.Equals(neighbor, to, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                    queue.Enqueue(neighbor);
                }
            }
        }

        if (!found)
            return [];

        // Reconstruct path from 'to' back to 'from'
        var path = new List<CodeGraphEdge>();
        var node = to;
        while (predecessor.TryGetValue(node, out var pred))
        {
            path.Add(pred.Edge);
            node = pred.Prev;
        }

        path.Reverse();
        return [.. path];
    }

    /// <summary>All edges involving a given file (as source or target).</summary>
    public CodeGraphEdge[] GetEdgesForFile(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        return _store.GetFileEdges(file);
    }
}
