namespace Ampelum.Samples.ComposedPrograms.Fake;

/// <summary>
/// Deterministic stand-in for a writer, used only by the scripted model. It reads the title and the
/// details with simple keyword rules; a real model replaces all of it.
/// </summary>
internal static class NoteWriter
{
    static readonly Dictionary<string, string> PastTense = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fix"] = "Fixed",
        ["remove"] = "Removed",
        ["rename"] = "Renamed",
        ["refactor"] = "Refactored",
        ["upgrade"] = "Upgraded",
        ["cleanup"] = "Cleaned up",
    };

    public static string Kind(string title, string details)
    {
        var first = FirstWord(title);
        if (first.Equals("fix", StringComparison.OrdinalIgnoreCase))
            return "Fix";
        if (first is "Remove" or "Rename" || details.Contains("must call", StringComparison.OrdinalIgnoreCase)
            || details.Contains("update your", StringComparison.OrdinalIgnoreCase))
            // The scripted model is sloppy about the case of this label, like a weak real one.
            return "breaking";
        if (first is "Refactor" or "Cleanup" or "Upgrade" || details.Contains("internal", StringComparison.OrdinalIgnoreCase))
            return "Internal";
        return "Feature";
    }

    public static int Impact(string kind, string title) => kind.ToLowerInvariant() switch
    {
        "breaking" => 4,
        "fix" => title.Contains("charge", StringComparison.OrdinalIgnoreCase)
                 || title.Contains("crash", StringComparison.OrdinalIgnoreCase) ? 4 : 2,
        "internal" => 1,
        _ => 3,
    };

    /// <summary>A verb-first note that names the change.</summary>
    public static string Good(string title)
    {
        var first = FirstWord(title);
        var rest = title.Length > first.Length ? title[first.Length..].TrimStart() : "";
        return PastTense.TryGetValue(first, out var verb)
            ? $"{verb} {rest}.".Replace("  ", " ")
            : $"Added {LowerFirst(title)}.";
    }

    /// <summary>A note that is correct but says nothing about the change.</summary>
    public static string Weak() => "Improved the product.";

    /// <summary>A note that names the change and then wanders.</summary>
    public static string Verbose(string title) =>
        $"{Good(title).TrimEnd('.')} together with a number of smaller related improvements customers asked for.";

    /// <summary>The title, used as the note: what a program that was given no style guidance writes.</summary>
    public static string Plain(string title) => title.TrimEnd('.') + ".";

    static string FirstWord(string text) => text.Split(' ', 2)[0];

    static string LowerFirst(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
