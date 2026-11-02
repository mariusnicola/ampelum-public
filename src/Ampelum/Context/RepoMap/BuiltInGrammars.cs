using System.Text.RegularExpressions;

namespace SignalGrid.Ampelum.Context.RepoMap;

/// <summary>Bundled regex-based ILanguageGrammar implementations for 10 languages.</summary>
public static class BuiltInGrammars
{
    /// <summary>Returns the 10 bundled Tier-1 grammars.</summary>
    public static IReadOnlyList<ILanguageGrammar> GetAll() => [
        new CSharpGrammar(), new TypeScriptGrammar(), new GoGrammar(), new RustGrammar(),
        new PythonGrammar(), new JavaGrammar(), new SqlGrammar(), new YamlGrammar(),
        new MarkdownGrammar(), new TomlGrammar()
    ];

    private static int Line(int index, string source) => source[..index].Count(c => c == '\n') + 1;
    private static int Col(int index, string source) => index - source.LastIndexOf('\n', index);

    private sealed class CSharpGrammar : ILanguageGrammar
    {
        public string Language => "C#";
        public IReadOnlySet<string> Extensions => new HashSet<string> { ".cs" };
        public IReadOnlyList<SymbolDef> ExtractSymbols(string source, string path)
        {
            var r = new Regex(@"(?:public|private|protected|internal|static|sealed|abstract|partial\s+)?(?:class|record|struct)\s+(\w+)");
            var r2 = new Regex(@"(?:public|private|protected|internal\s+)?interface\s+(\w+)");
            var r3 = new Regex(@"(?:public|private|protected|internal|static|async|unsafe|virtual|override|sealed)\s+(?:\w+\s+)*(\w+)\s+(\w+)\s*\(");
            var list = new List<SymbolDef>();
            foreach (Match m in r.Matches(source)) list.Add(new("class", m.Groups[1].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            foreach (Match m in r2.Matches(source)) list.Add(new("interface", m.Groups[1].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            foreach (Match m in r3.Matches(source)) list.Add(new("method", m.Groups[2].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            return list;
        }
    }

    private sealed class TypeScriptGrammar : ILanguageGrammar
    {
        public string Language => "TypeScript";
        public IReadOnlySet<string> Extensions => new HashSet<string> { ".ts", ".tsx", ".js", ".jsx" };
        public IReadOnlyList<SymbolDef> ExtractSymbols(string source, string path)
        {
            var r1 = new Regex(@"(?:export\s+)?(?:abstract\s+)?class\s+(\w+)");
            var r2 = new Regex(@"(?:export\s+)?(?:async\s+)?function\s+(\w+)");
            var r3 = new Regex(@"(?:export\s+)?interface\s+(\w+)");
            var list = new List<SymbolDef>();
            foreach (Match m in r1.Matches(source)) list.Add(new("class", m.Groups[1].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            foreach (Match m in r2.Matches(source)) list.Add(new("function", m.Groups[1].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            foreach (Match m in r3.Matches(source)) list.Add(new("interface", m.Groups[1].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            return list;
        }
    }

    private sealed class GoGrammar : ILanguageGrammar
    {
        public string Language => "Go";
        public IReadOnlySet<string> Extensions => new HashSet<string> { ".go" };
        public IReadOnlyList<SymbolDef> ExtractSymbols(string source, string path)
        {
            var r1 = new Regex(@"func\s+(?:\([^)]*\)\s+)?(\w+)\s*\(");
            var r2 = new Regex(@"type\s+(\w+)\s+struct");
            var list = new List<SymbolDef>();
            foreach (Match m in r1.Matches(source)) list.Add(new("function", m.Groups[1].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            foreach (Match m in r2.Matches(source)) list.Add(new("struct", m.Groups[1].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            return list;
        }
    }

    private sealed class RustGrammar : ILanguageGrammar
    {
        public string Language => "Rust";
        public IReadOnlySet<string> Extensions => new HashSet<string> { ".rs" };
        public IReadOnlyList<SymbolDef> ExtractSymbols(string source, string path)
        {
            var r1 = new Regex(@"fn\s+(\w+)\s*\(");
            var r2 = new Regex(@"struct\s+(\w+)");
            var r3 = new Regex(@"impl\s+(\w+)");
            var list = new List<SymbolDef>();
            foreach (Match m in r1.Matches(source)) list.Add(new("function", m.Groups[1].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            foreach (Match m in r2.Matches(source)) list.Add(new("struct", m.Groups[1].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            foreach (Match m in r3.Matches(source)) list.Add(new("impl", m.Groups[1].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            return list;
        }
    }

    private sealed class PythonGrammar : ILanguageGrammar
    {
        public string Language => "Python";
        public IReadOnlySet<string> Extensions => new HashSet<string> { ".py" };
        public IReadOnlyList<SymbolDef> ExtractSymbols(string source, string path)
        {
            var r1 = new Regex(@"class\s+(\w+)");
            var r2 = new Regex(@"def\s+(\w+)\s*\(");
            var list = new List<SymbolDef>();
            foreach (Match m in r1.Matches(source)) list.Add(new("class", m.Groups[1].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            foreach (Match m in r2.Matches(source)) list.Add(new("function", m.Groups[1].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            return list;
        }
    }

    private sealed class JavaGrammar : ILanguageGrammar
    {
        public string Language => "Java";
        public IReadOnlySet<string> Extensions => new HashSet<string> { ".java" };
        public IReadOnlyList<SymbolDef> ExtractSymbols(string source, string path)
        {
            var r1 = new Regex(@"(?:public|private|protected|static|final|abstract)\s+(?:class|interface|enum)\s+(\w+)");
            var r2 = new Regex(@"(?:public|private|protected|static|final)\s+(?:\w+\s+)*(\w+)\s+(\w+)\s*\(");
            var list = new List<SymbolDef>();
            foreach (Match m in r1.Matches(source)) list.Add(new("class", m.Groups[1].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            foreach (Match m in r2.Matches(source)) list.Add(new("method", m.Groups[2].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            return list;
        }
    }

    private sealed class SqlGrammar : ILanguageGrammar
    {
        public string Language => "SQL";
        public IReadOnlySet<string> Extensions => new HashSet<string> { ".sql" };
        public IReadOnlyList<SymbolDef> ExtractSymbols(string source, string path)
        {
            var r1 = new Regex(@"CREATE\s+(?:TABLE|TABLE\s+IF\s+NOT\s+EXISTS)\s+(?:\w+\.)?(\w+)", RegexOptions.IgnoreCase);
            var r2 = new Regex(@"CREATE\s+(?:OR\s+REPLACE\s+)?VIEW\s+(?:\w+\.)?(\w+)", RegexOptions.IgnoreCase);
            var list = new List<SymbolDef>();
            foreach (Match m in r1.Matches(source)) list.Add(new("table", m.Groups[1].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            foreach (Match m in r2.Matches(source)) list.Add(new("view", m.Groups[1].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            return list;
        }
    }

    private sealed class YamlGrammar : ILanguageGrammar
    {
        public string Language => "YAML";
        public IReadOnlySet<string> Extensions => new HashSet<string> { ".yaml", ".yml" };
        public IReadOnlyList<SymbolDef> ExtractSymbols(string source, string path)
        {
            var r = new Regex(@"^\s*(\w[\w-]*):", RegexOptions.Multiline);
            var list = new List<SymbolDef>();
            foreach (Match m in r.Matches(source)) list.Add(new("key", m.Groups[1].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            return list;
        }
    }

    private sealed class MarkdownGrammar : ILanguageGrammar
    {
        public string Language => "Markdown";
        public IReadOnlySet<string> Extensions => new HashSet<string> { ".md", ".mdx" };
        public IReadOnlyList<SymbolDef> ExtractSymbols(string source, string path)
        {
            var r = new Regex(@"^(#{1,6})\s+(.+)$", RegexOptions.Multiline);
            var list = new List<SymbolDef>();
            foreach (Match m in r.Matches(source)) list.Add(new("heading", m.Groups[2].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            return list;
        }
    }

    private sealed class TomlGrammar : ILanguageGrammar
    {
        public string Language => "TOML";
        public IReadOnlySet<string> Extensions => new HashSet<string> { ".toml" };
        public IReadOnlyList<SymbolDef> ExtractSymbols(string source, string path)
        {
            var r1 = new Regex(@"^\[(\w[\w.-]*)\]", RegexOptions.Multiline);
            var r2 = new Regex(@"^(\w[\w.-]*)\s*=", RegexOptions.Multiline);
            var list = new List<SymbolDef>();
            foreach (Match m in r1.Matches(source)) list.Add(new("section", m.Groups[1].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            foreach (Match m in r2.Matches(source)) list.Add(new("key", m.Groups[1].Value, Line(m.Index, source), Col(m.Index, source), 0, 0));
            return list;
        }
    }
}