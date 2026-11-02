using SignalGrid.Ampelum.Cli.Commands;
using Shouldly;
using Xunit;

namespace Ampelum.Cli.Tests;

public class BenchCommandTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ampelum-bench-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Scaffolds_BenchmarkDotNetHarness_FromTemplate()
    {
        var runner = new RecordingProcessRunner();
        var config = RootCommandFactory.Create(runner);

        var (exit, _, _) = await StdoutCapture.RunAsync(
            config,
            $"bench --signature MySig --output {_tempDir}",
            TestContext.Current.CancellationToken);

        exit.ShouldBe(0);
        var program = File.ReadAllText(Path.Combine(_tempDir, "Program.cs"));
        program.ShouldContain("BenchmarkRunner.Run<MySigBench>()");
        program.ShouldContain("public class MySigBench");

        var csproj = File.ReadAllText(Path.Combine(_tempDir, "MySig.Benchmarks.csproj"));
        csproj.ShouldContain("<PackageReference Include=\"BenchmarkDotNet\"");
        csproj.ShouldContain("<TargetFramework>net10.0</TargetFramework>");
    }

    [Fact]
    public void Scaffolder_Creates_OutputDirectory_IfMissing()
    {
        var nested = Path.Combine(_tempDir, "nested");
        BenchHarnessScaffolder.Write("Foo", nested);
        Directory.Exists(nested).ShouldBeTrue();
        File.Exists(Path.Combine(nested, "Foo.Benchmarks.csproj")).ShouldBeTrue();
    }
}
