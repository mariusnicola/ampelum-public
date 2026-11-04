using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SignalGrid.Ampelum.Agent.CodeIntelligence;

/// <summary>Extracts symbols from source files using regex fallback when tree-sitter is unavailable.</summary>
public sealed class CodeIndexer
{
    private const int MethodPattern = 1;
    private static readonly Regex[] PatternCache = CreatePatterns();
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs", ".java",
        ".rb", ".php", ".c", ".cpp", ".h", ".hpp", ".swift", ".kt", ".scala",
        ".dart", ".fs", ".fsx", ".m", ".mm"
    };

    private static Regex[] CreatePatterns() => [
        new(@"^\s*(public|private|internal|protected|static|sealed|abstract|partial|readonly)?\s*(public|private|internal|protected|static|sealed|abstract|partial|readonly)?\s*(class|interface|struct|enum|record)\s+(\w+)", RegexOptions.Multiline | RegexOptions.Compiled),
        // MethodPattern: optional modifiers, a return type (plain, generic or array), the name, then a parameter list.
        // Keywords that start a statement or a type declaration are refused, so "return Foo(x);" is no method and
        // "record Money(int X)" stays a record. [ \t] instead of \s keeps the match on the declaration line.
        new(@"^[ \t]*(?:(?:public|private|internal|protected|static|sealed|abstract|virtual|override|async|extern|unsafe)[ \t]+)*(?!(?:return|new|await|throw|else|if|for|foreach|while|switch|using|lock|catch|yield|var|case|goto|do|try|finally|fixed|checked|unchecked|class|interface|struct|enum|record|def|fn|func|function|namespace|impl|trait|type)\b)[\w.?]+(?:<[^()\r\n]*>)?(?:\[\])*[ \t]+(\w+)(?:<[^()\r\n]*>)?[ \t]*\(.*\)", RegexOptions.Multiline | RegexOptions.Compiled),
        new(@"^\s*(async\s+)?def\s+(\w+)\s*\(.*\):", RegexOptions.Multiline | RegexOptions.Compiled),
        new(@"^\s*class\s+(\w+)(\(.*\))?:", RegexOptions.Multiline | RegexOptions.Compiled),
        new(@"^\s*func\s+(\([^)]*\)\s*)?(\w+)\s*\(.*?\)\s*(\([^)]*\)|\w+)?\s*\{?", RegexOptions.Multiline | RegexOptions.Compiled),
        new(@"^\s*(pub\s+)?(async\s+)?fn\s+(\w+)\s*[<(]", RegexOptions.Multiline | RegexOptions.Compiled),
        new(@"^\s*(pub\s+)?(struct|enum|trait|impl|type)\s+(\w+)", RegexOptions.Multiline | RegexOptions.Compiled),
        new(@"^\s*(export\s+)?(async\s+)?function\s+(\w+)", RegexOptions.Multiline | RegexOptions.Compiled),
        new(@"^\s*(export\s+)?class\s+(\w+)", RegexOptions.Multiline | RegexOptions.Compiled),
        new(@"^\s*(export\s+)?interface\s+(\w+)", RegexOptions.Multiline | RegexOptions.Compiled),
    ];

    public ExtractResult Extract(string filePath)
    {
        try
        {
            var ext = Path.GetExtension(filePath);
            if (!SourceExtensions.Contains(ext))
                return new ExtractResult(filePath, [], null);

            var source = File.ReadAllText(filePath);
            var lines = source.Split('\n');
            var symbols = new List<Symbol>();

            for (var patternIndex = 0; patternIndex < PatternCache.Length; patternIndex++)
            {
                foreach (Match match in PatternCache[patternIndex].Matches(source))
                {
                    var lineIdx = source[..match.Index].Count(c => c == '\n');
                    var line = lineIdx + 1; // 1-indexed
                    var name = match.Groups.Values.LastOrDefault(g => g.Success && g.Index > match.Groups[0].Index && !int.TryParse(g.Value, out _))?.Value ?? "?";
                    // The method pattern's text holds no kind word of its own; a name such as GetEnumerator must not read as an enum.
                    var kind = patternIndex == MethodPattern
                        ? (ext is ".cs" or ".java" or ".kt" ? SymbolKind.Method : SymbolKind.Function)
                        : InferKind(match, ext);
                    var sig = lines[lineIdx].Trim();
                    var (lineStart, lineEnd) = FindBounds(lines, line, match);
                    var hash = Symbol.ComputeHash(source, lineStart, lineEnd);
                    symbols.Add(new Symbol(filePath, lineStart, lineEnd, kind, name, sig, null, hash));
                }
            }

            var deduped = symbols
                .GroupBy(s => (s.File, s.Name, s.LineStart))
                .Select(g => g.First())
                .OrderBy(s => s)
                .ToArray();

            var edges = new List<CodeGraphEdge>();
            edges.AddRange(ExtractImports(lines, filePath));
            edges.AddRange(ExtractCalls(lines, deduped));

            return new ExtractResult(filePath, deduped, null, [.. edges]);
        }
        catch (Exception ex)
        {
            return new ExtractResult(filePath, [], ex.Message);
        }
    }

    public ExtractResult[] ExtractDirectory(string directory, string[]? ignorePatterns = null)
    {
        ignorePatterns ??= ["bin", "obj", "node_modules", ".git", ".ampelum", "dist", "build", ".venv"];
        var results = new ConcurrentBag<ExtractResult>();
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => !ignorePatterns.Any(p => f.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .Where(f => SourceExtensions.Contains(Path.GetExtension(f)))
            .ToArray();

        Parallel.ForEach(files, file =>
        {
            results.Add(Extract(file));
        });

        return [.. results.OrderBy(r => r.File)];
    }

    public static bool IsSupported(string path) =>
        SourceExtensions.Contains(Path.GetExtension(path));

    private static SymbolKind InferKind(Match match, string ext)
    {
        var text = match.Value;
        if (text.Contains("class", StringComparison.OrdinalIgnoreCase)) return SymbolKind.Class;
        if (text.Contains("interface", StringComparison.OrdinalIgnoreCase)) return SymbolKind.Interface;
        if (text.Contains("struct", StringComparison.OrdinalIgnoreCase)) return SymbolKind.Struct;
        if (text.Contains("enum", StringComparison.OrdinalIgnoreCase)) return SymbolKind.Enum;
        if (text.Contains("record", StringComparison.OrdinalIgnoreCase)) return SymbolKind.Record;
        if (text.Contains("function", StringComparison.OrdinalIgnoreCase) || text.Contains("fn ") || text.Contains("def ") || text.Contains("func ")) return SymbolKind.Function;
        if (text.Contains("property", StringComparison.OrdinalIgnoreCase)) return SymbolKind.Property;
        return ext is ".cs" or ".java" or ".kt" ? SymbolKind.Method : SymbolKind.Function;
    }

    /// <summary>Finds the actual symbol boundary by tracking brace depth from the declaration line.</summary>
    private static (int LineStart, int LineEnd) FindBounds(string[] lines, int matchLine, Match match)
    {
        int depth = 0;
        int endLine = matchLine;

        for (int i = matchLine - 1; i < lines.Length; i++)
        {
            foreach (char c in lines[i])
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }
            if (depth == 0 && i > matchLine - 1)
            {
                endLine = i + 1;
                break;
            }
        }

        if (depth != 0) endLine = lines.Length;

        return (matchLine, endLine);
    }

    /// <summary>Extracts import/using edges from source lines.</summary>
    private static CodeGraphEdge[] ExtractImports(string[] lines, string filePath)
    {
        var edges = new List<CodeGraphEdge>();
        var importRegex = new Regex(@"^\s*(using|import)\s+([^;]+);", RegexOptions.Multiline | RegexOptions.Compiled);

        for (int i = 0; i < lines.Length; i++)
        {
            var match = importRegex.Match(lines[i]);
            if (match.Success)
            {
                var imported = match.Groups[2].Value.Trim();
                edges.Add(new CodeGraphEdge(filePath, i + 1, "<file>", null, null, imported, EdgeType.Imports, i + 1));
            }
        }

        return [.. edges];
    }

    /// <summary>Extracts method call edges by scanning symbol bodies for Foo( patterns.</summary>
    private static CodeGraphEdge[] ExtractCalls(string[] lines, Symbol[] symbols)
    {
        var edges = new List<CodeGraphEdge>();
        var callRegex = new Regex(@"(\w+)\s*\(", RegexOptions.Compiled);

        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "if", "for", "while", "switch", "catch", "foreach", "return", "throw",
            "var", "new", "base", "this", "await", "yield", "using"
        };

        foreach (var sym in symbols.Where(s => s.Kind is SymbolKind.Method or SymbolKind.Function))
        {
            for (int i = sym.LineStart; i <= sym.LineEnd && i <= lines.Length; i++)
            {
                if (i < 1 || i > lines.Length) continue;
                var line = lines[i - 1];
                foreach (Match match in callRegex.Matches(line))
                {
                    var called = match.Groups[1].Value;
                    if (called == sym.Name) continue;
                    if (keywords.Contains(called)) continue;

                    edges.Add(new CodeGraphEdge(
                        sym.File, sym.LineStart, sym.Name,
                        sym.File, i, called, EdgeType.Call, i));
                }
            }
        }

        return [.. edges];
    }
}
