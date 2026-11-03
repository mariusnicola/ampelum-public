using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.CodeIntelligence;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>What the code folder says about one issue: types that match its title, what their files import, and which conventions those files break.</summary>
public sealed record IssueFacts(
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string> Imports,
    IReadOnlyList<string> Violations,
    IReadOnlyList<string> Files);

/// <summary>
/// Indexes the project's source folder (type symbols and import edges, no embeddings) and checks its files against the
/// project's convention manifest. Reads stay inside <see cref="BlastRadiusScope"/> given by the caller.
/// </summary>
public sealed class CodeFacts : IDisposable
{
    const int MinWordLength = 4;

    readonly string _root;
    readonly BlastRadiusScope _readable;
    readonly IndexStore _store;
    readonly CodeGraph _graph;
    readonly DefaultConventionEnforcer _conventions;

    /// <summary>Indexes the <paramref name="sourceFolders"/> under <paramref name="root"/>.</summary>
    public CodeFacts(string root, IEnumerable<string> sourceFolders, BlastRadiusScope readable)
    {
        _root = root;
        _readable = readable;
        _store = new IndexStore(root);
        _graph = new CodeGraph(_store);

        try
        {
            // An empty ignore list: the default one is matched against the whole absolute path, so a project that
            // lives under a folder named "build" or "bin" would index nothing.
            var results = sourceFolders.SelectMany(folder => new CodeIndexer().ExtractDirectory(folder, ignorePatterns: [])).ToArray();
            _store.BatchUpsert([.. results.Where(r => r.Error is null).Select(r => (r.File, r.Symbols))]);
            foreach (var result in results.Where(r => r.Error is null))
                _store.UpsertEdges(result.File, result.Edges ?? []);

            _conventions = new DefaultConventionEnforcer(root);
            var manifest = Path.Combine(root, ".ampelum-conventions.yaml");
            // The enforcer lets an AMPELUM_CONVENTIONS variable replace the project manifest; a run must use the project's own.
            if (!string.Equals(_conventions.ManifestSourcePath, manifest, StringComparison.Ordinal))
                throw new InvalidOperationException($"Conventions came from '{_conventions.ManifestSourcePath ?? "built-in defaults"}' instead of '{manifest}'. Unset AMPELUM_CONVENTIONS.");
        }
        catch
        {
            // The caller never gets an object to dispose, so the index file would stay locked.
            _store.Dispose();
            throw;
        }
    }

    /// <summary>Symbols found in the index.</summary>
    public int SymbolCount => _store.TotalSymbols;

    /// <summary>Collects the facts for one issue from the files the scope allows.</summary>
    public async Task<IssueFacts> GatherAsync(TriageIssue issue, CancellationToken ct = default)
    {
        var words = issue.Title.Split([' ', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length >= MinWordLength)
            .ToArray();

        var matches = _store.GetAllSymbols()
            .Where(s => _readable.ContainsPath(s.File))
            .Where(s => words.Any(w => s.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var files = matches.Select(s => s.File).Distinct().Order().ToArray();
        var imports = files
            .SelectMany(_graph.GetEdgesForFile)
            .Where(e => e.EdgeType == EdgeType.Imports)
            .Select(e => $"{Relative(e.SrcFile)} imports {e.TgtName}")
            .Distinct()
            .Order()
            .ToArray();
        var violations = new List<string>();
        foreach (var file in files)
        {
            var content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            foreach (var violation in await _conventions.CheckAsync(Relative(file), content, ct).ConfigureAwait(false))
                violations.Add($"{violation.File}: {violation.Rule} ({violation.Severity}) {violation.Message}");
        }

        return new IssueFacts(
            // Line numbers are left out on purpose: the regex indexer reports the line of the blank space before a declaration.
            [.. matches.Select(s => $"{s.Kind} {s.Name} in {Relative(s.File)}").Order()],
            imports,
            violations,
            [.. files.Select(Relative)]);
    }

    /// <summary>Path relative to the project root, with forward slashes.</summary>
    public string Relative(string path) => Path.GetRelativePath(_root, path).Replace("\\", "/", StringComparison.Ordinal);

    public void Dispose() => _store.Dispose();
}
