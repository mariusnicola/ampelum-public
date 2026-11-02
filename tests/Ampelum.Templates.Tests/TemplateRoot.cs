namespace Ampelum.Templates.Tests;

internal static class TemplateRoot
{
    /// <summary>Walks up from the test runtime base directory to find the repository root by locating SignalGrid.Ampelum.sln.</summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SignalGrid.Ampelum.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"Could not locate repository root (SignalGrid.Ampelum.sln) from '{AppContext.BaseDirectory}'.");
    }

    public static string TemplateDir(string shortName)
        => Path.Combine(FindRepoRoot(), "src", "Ampelum.Templates", "templates", shortName);
}
