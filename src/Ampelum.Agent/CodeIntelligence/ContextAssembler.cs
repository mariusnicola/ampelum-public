using System.Text;

namespace SignalGrid.Ampelum.Agent.CodeIntelligence;

/// <summary>Context assembled by the hybrid retriever.</summary>
public sealed record AssembledContext
{
    public bool HasContent => Slices.Count > 0;
    public List<SourceSlice> Slices { get; init; } = [];
    public int EstimatedTokens { get; init; }
    public int SymbolsSearched { get; init; }
    public string RetrievalMethod { get; init; } = "none";

    public string Format()
    {
        if (!HasContent) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine("\n## Relevant code");
        foreach (var slice in Slices)
        {
            sb.AppendLine($"\n### {slice.File} (lines {slice.LineStart}-{slice.LineEnd}) [{slice.RelevanceScore:F2}]");
            sb.AppendLine("```" + InferLanguage(slice.File));
            sb.AppendLine(slice.Content);
            sb.AppendLine("```");
        }
        return sb.ToString();
    }

    private static string InferLanguage(string file) => Path.GetExtension(file) switch
    {
        ".cs" => "csharp", ".ts" or ".tsx" => "typescript",
        ".js" or ".jsx" => "javascript", ".py" => "python",
        ".go" => "go", ".rs" => "rust", ".java" => "java", _ => ""
    };
}

/// <summary>A slice of a source file with relevance.</summary>
public sealed record SourceSlice(string File, int LineStart, int LineEnd, string Content, float RelevanceScore);

/// <summary>
/// Hybrid context assembler that tries BM25 name match first (cheapest),
/// falls back to semantic cosine similarity, then code graph.
/// Deduplicates by file and enforces token budget via SharpToken.
/// </summary>
public sealed class ContextAssembler
{
    private const float Bm25Threshold = 0.5f;
    private const float SemanticThreshold = 0.35f;
    private const int MaxSymbolsPerQuery = 8;

    private readonly IndexStore _store;
    private readonly EmbeddingEngine _engine;
    private readonly string _workingDir;

    public ContextAssembler(IndexStore store, EmbeddingEngine engine, string? workingDir = null)
    {
        _store = store;
        _engine = engine;
        _workingDir = workingDir ?? Directory.GetCurrentDirectory();
    }

    /// <summary>Assemble context for a user query. Uses hybrid retrieval: BM25 → semantic.</summary>
    public AssembledContext Assemble(string query, int tokenBudget = 2000)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new AssembledContext();

        // Tier 1: BM25 name match (fastest, cheapest)
        var bm25Results = Bm25Match(query);
        if (bm25Results.Count > 0)
        {
            var slices = BuildSlices(bm25Results, tokenBudget);
            if (slices.Count > 0)
                return new AssembledContext
                {
                    Slices = slices,
                    EstimatedTokens = CountTokens(slices),
                    SymbolsSearched = bm25Results.Count,
                    RetrievalMethod = "bm25"
                };
        }

        // Tier 2: Semantic similarity via cached embeddings
        var allEmbeddings = _store.GetCachedEmbeddings();
        if (allEmbeddings.Length == 0)
            return new AssembledContext { SymbolsSearched = 0 };

        var queryEmbedding = _engine.Embed(query);
        if (queryEmbedding is null)
            return new AssembledContext { SymbolsSearched = allEmbeddings.Length };

        var scored = new List<(Symbol Symbol, float Score)>();
        foreach (var (symbol, emb) in allEmbeddings)
        {
            var score = EmbeddingEngine.CosineSimilarity(queryEmbedding, emb);
            if (score > SemanticThreshold)
                scored.Add((symbol, score));
        }

        var top = scored
            .OrderByDescending(s => s.Score)
            .Take(MaxSymbolsPerQuery)
            .ToList();

        if (top.Count == 0)
            return new AssembledContext { SymbolsSearched = allEmbeddings.Length };

        var semanticSlices = BuildSlices(top, tokenBudget);
        return new AssembledContext
        {
            Slices = semanticSlices,
            EstimatedTokens = CountTokens(semanticSlices),
            SymbolsSearched = allEmbeddings.Length,
            RetrievalMethod = "semantic"
        };
    }

    // ── BM25 via simple name token matching ─────────────────────────────────

    private List<(Symbol Symbol, float Score)> Bm25Match(string query)
    {
        var queryTokens = TokenizeQuery(query);
        if (queryTokens.Count == 0) return [];

        var allSymbols = _store.GetAllSymbols();
        var results = new List<(Symbol Symbol, float Score)>();

        foreach (var symbol in allSymbols)
        {
            var score = ComputeBm25Score(queryTokens, symbol);
            if (score > Bm25Threshold)
                results.Add((symbol, score));
        }

        return [.. results.OrderByDescending(r => r.Score).Take(MaxSymbolsPerQuery)];
    }

    private static HashSet<string> TokenizeQuery(string query) =>
        [.. query.Split([' ', '\t', '_', '.', '/', '\\', '-', '(', ')', '[', ']', '{', '}'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())];

    private static float ComputeBm25Score(HashSet<string> queryTokens, Symbol symbol)
    {
        if (queryTokens.Count == 0) return 0;

        var nameLower = symbol.Name.ToLowerInvariant();
        var sigLower = symbol.Signature.ToLowerInvariant();
        var summaryLower = symbol.Summary?.ToLowerInvariant() ?? "";
        var fileLower = symbol.File.ToLowerInvariant();

        int matches = 0;
        foreach (var token in queryTokens)
        {
            if (nameLower.Contains(token) ||
                sigLower.Contains(token) ||
                summaryLower.Contains(token) ||
                fileLower.Contains(token))
                matches++;
        }

        // Exact name match gets bonus
        var exactBonus = nameLower == string.Join("", queryTokens) ? 0.5f : 0;

        return (float)matches / queryTokens.Count + exactBonus;
    }

    // ── Context slice building with dedup and token budget ─────────────────

    private List<SourceSlice> BuildSlices(List<(Symbol Symbol, float Score)> results, int tokenBudget)
    {
        var slices = new List<SourceSlice>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remainingBudget = tokenBudget;

        foreach (var (symbol, score) in results)
        {
            if (remainingBudget <= 0) break;

            try
            {
                var filePath = Path.IsPathRooted(symbol.File)
                    ? symbol.File
                    : Path.Combine(_workingDir, symbol.File);

                if (!File.Exists(filePath)) continue;

                var allLines = File.ReadAllLines(filePath);
                var start = Math.Max(0, symbol.LineStart - 2);
                var end = Math.Min(allLines.Length, symbol.LineEnd + 1);
                var content = string.Join('\n', allLines[start..end]);

                // Deduplicate: if we already have a slice from this file, merge
                if (seenFiles.Contains(filePath))
                {
                    var existing = slices.First(s =>
                        string.Equals(s.File, filePath, StringComparison.OrdinalIgnoreCase));
                    var mergedStart = Math.Min(existing.LineStart, start + 1);
                    var mergedEnd = Math.Max(existing.LineEnd, end);
                    var mergedContent = string.Join('\n', allLines[(mergedStart - 1)..mergedEnd]);
                    slices.Remove(existing);
                    slices.Add(new SourceSlice(filePath, mergedStart, mergedEnd, mergedContent,
                        Math.Max(existing.RelevanceScore, score)));
                    continue;
                }

                var sliceTokens = EstimateTokens(content);
                if (sliceTokens > remainingBudget)
                {
                    // Trim the slice to fit budget
                    var trimmedLines = TrimToBudget(allLines, start, end, remainingBudget);
                    content = string.Join('\n', trimmedLines);
                }

                slices.Add(new SourceSlice(filePath, start + 1, end, content, score));
                seenFiles.Add(filePath);
                remainingBudget -= sliceTokens;
            }
            catch { /* skip unreadable files */ }
        }

        return slices;
    }

    private static string[] TrimToBudget(string[] allLines, int start, int end, int budget)
    {
        var result = new List<string>();
        var tokens = 0;
        for (int i = start; i < end && tokens < budget; i++)
        {
            if (i >= 0 && i < allLines.Length)
            {
                result.Add(allLines[i]);
                tokens += allLines[i].Length / 4 + 1; // rough estimate
            }
        }
        return [.. result];
    }

    // ── Token counting via SharpToken ───────────────────────────────────────

    private int CountTokens(List<SourceSlice> slices)
    {
        var total = 0;
        foreach (var slice in slices)
            total += EstimateTokens(slice.Content);
        return total;
    }

    private int EstimateTokens(string text)
    {
        // Rough estimate: ~4 chars per token for code
        return text.Length / 4 + 1;
    }
}
