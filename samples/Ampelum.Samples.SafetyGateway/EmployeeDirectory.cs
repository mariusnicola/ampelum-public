namespace Ampelum.Samples.SafetyGateway;

public sealed record DirectoryEntry(string Name, string Team, string Note);

/// <summary>The system the search tool talks to. It only answers callers that present its credential.</summary>
public interface IEmployeeDirectory
{
    IReadOnlyList<DirectoryEntry>? Search(string credential, string query);
}

/// <summary>In-memory stand-in for a real directory service.</summary>
public sealed class InMemoryDirectory(IReadOnlyList<DirectoryEntry> entries, string acceptedCredential) : IEmployeeDirectory
{
    public IReadOnlyList<DirectoryEntry>? Search(string credential, string query) =>
        credential != acceptedCredential
            ? null
            : entries.Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                              || e.Team.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
}

/// <summary>The handbook the read and write tools work on.</summary>
public sealed class HandbookStore(IDictionary<string, string> documents)
{
    public string? Read(string name) => documents.TryGetValue(name, out var text) ? text : null;

    public void Write(string name, string content) => documents[name] = content;

    public IReadOnlyCollection<string> Names => documents.Keys.ToArray();
}
