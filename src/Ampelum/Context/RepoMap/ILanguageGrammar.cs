namespace SignalGrid.Ampelum.Context.RepoMap;

/// <summary>Defines how a programming language's structural elements are extracted for repo-mapping.</summary>
public interface ILanguageGrammar
{
    /// <summary>Language name (e.g., "C#", "TypeScript").</summary>
    string Language { get; }
    /// <summary>File extensions this grammar handles (e.g., ".cs", ".ts", ".tsx").</summary>
    IReadOnlySet<string> Extensions { get; }
    /// <summary>Extracts named symbols (classes, functions, interfaces, etc) from source code text.</summary>
    IReadOnlyList<SymbolDef> ExtractSymbols(string sourceText, string filePath);
}

/// <summary>A symbol extracted by a language grammar.</summary>
public sealed record SymbolDef(string Kind, string Name, int Line, int Column, int EndLine, int EndColumn);