using System.Text.RegularExpressions;

namespace SignalGrid.Ampelum.Context.Subagent;

/// <summary>Validates subagent inputs for path containment, length limits, and allowed patterns.</summary>
public sealed partial class SubagentInputValidator
{
    /// <summary>Maximum allowed input length in characters.</summary>
    public const int MaxInputLength = 100_000;

    /// <summary>Validates that a subagent input is safe to process. Returns null if valid, error message if invalid.</summary>
    public string? Validate(string input, string fieldName)
    {
        if (string.IsNullOrEmpty(input))
            return $"{fieldName} is empty";
        if (input.Length > MaxInputLength)
            return $"{fieldName} exceeds {MaxInputLength} characters";
        if (ControlCharRegex().IsMatch(input))
            return $"{fieldName} contains control characters";
        return null;
    }

    /// <summary>Validates that a file path is contained within the allowed root directory.</summary>
    public string? ValidatePath(string path, string allowedRoot)
    {
        if (string.IsNullOrEmpty(path))
            return "Path is empty";
        // Resolve relative path
        var full = Path.GetFullPath(Path.Combine(allowedRoot, path));
        var root = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return $"Path '{path}' escapes allowed root '{allowedRoot}'";
        return null;
    }

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F]")]
    private static partial Regex ControlCharRegex();
}
