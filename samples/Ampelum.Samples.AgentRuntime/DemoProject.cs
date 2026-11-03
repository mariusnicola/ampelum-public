using System.Text.RegularExpressions;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>One backlog line: number, title and the labels in brackets.</summary>
public sealed record TriageIssue(int Number, string Title, IReadOnlyList<string> Labels)
{
    /// <summary>Short text used in prompts and logs.</summary>
    public string Headline => $"#{Number} {Title}";
}

/// <summary>The small code folder the run triages. It ships inside the assembly and is written to a working folder first.</summary>
public static partial class DemoProject
{
    const string Folder = "DemoProject";

    /// <summary>Writes the demo folder under <paramref name="directory"/> and returns that path.</summary>
    public static string Extract(string directory)
    {
        foreach (var name in Embedded.Names(Folder))
        {
            var target = Path.Combine(directory, name[(Folder.Length + 1)..].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, Embedded.ReadText(name));
        }
        return directory;
    }

    /// <summary>Reads the open issues from backlog.md in the project root.</summary>
    public static IReadOnlyList<TriageIssue> ReadBacklog(string root) =>
        [.. File.ReadAllLines(Path.Combine(root, "backlog.md"))
            .Select(line => IssueLine().Match(line))
            .Where(match => match.Success)
            .Select(match => new TriageIssue(
                int.Parse(match.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture),
                match.Groups["title"].Value,
                match.Groups["labels"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))];

    [GeneratedRegex(@"^- \[ \] #(?<number>\d+) (?<title>.+?) \((?<labels>[^)]*)\)\s*$")]
    private static partial Regex IssueLine();
}
