using System.IO;
using System.IO.Compression;
using System.Linq;
using Shouldly;
using Xunit;

namespace Ampelum.Analyzers.Tests;

public sealed class PackagingTests
{
    [Fact]
    public void PackedNupkg_ContainsAnalyzerUnderAnalyzersDotnetCs()
    {
        var repoRoot = FindRepoRoot();
        var packDir = Path.Combine(repoRoot, "src", "Ampelum.Analyzers", "bin", "Release");
        Directory.Exists(packDir).ShouldBeTrue($"Run 'dotnet pack -c Release src/Ampelum.Analyzers' first. Missing: {packDir}");
        var nupkg = Directory.EnumerateFiles(packDir, "SignalGrid.Ampelum.Analyzers.*.nupkg").FirstOrDefault();
        nupkg.ShouldNotBeNull("nupkg not produced.");
        using var zip = ZipFile.OpenRead(nupkg!);
        zip.Entries.Any(e => e.FullName.Replace('\\', '/') == "analyzers/dotnet/cs/SignalGrid.Ampelum.Analyzers.dll")
            .ShouldBeTrue("Analyzer DLL must be packed under analyzers/dotnet/cs/.");
        zip.Entries.Any(e => e.FullName.StartsWith("lib/", System.StringComparison.OrdinalIgnoreCase))
            .ShouldBeFalse("Analyzer must not ship runtime assets under lib/.");
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SignalGrid.Ampelum.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"Could not locate repository root (SignalGrid.Ampelum.sln) from '{System.AppContext.BaseDirectory}'.");
    }
}
