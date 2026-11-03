using System.Reflection;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>Reads the files this sample ships as embedded resources.</summary>
internal static class Embedded
{
    static readonly Assembly Source = typeof(Embedded).Assembly;

    /// <summary>Names of the embedded files under a folder prefix, with forward slashes.</summary>
    public static IEnumerable<string> Names(string folder) =>
        Source.GetManifestResourceNames()
            .Select(Normalise)
            .Where(name => name.StartsWith(folder + "/", StringComparison.Ordinal));

    /// <summary>Reads one embedded file by its forward-slash name.</summary>
    public static string ReadText(string name)
    {
        // MSBuild writes the folder separator of the build machine into the resource name.
        var actual = Source.GetManifestResourceNames().SingleOrDefault(n => Normalise(n) == name)
            ?? throw new InvalidOperationException($"Embedded file '{name}' is missing.");
        using var stream = Source.GetManifestResourceStream(actual)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    static string Normalise(string name) => name.Replace("\\", "/", StringComparison.Ordinal);
}
