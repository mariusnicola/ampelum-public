namespace SignalGrid.Ampelum.Cli.Commands;

/// <summary>Writes a minimal BenchmarkDotNet harness scaffolded against an Ampelum signature.</summary>
internal static class BenchHarnessScaffolder
{
    private const string ProgramTemplate = """
        using BenchmarkDotNet.Attributes;
        using BenchmarkDotNet.Running;

        BenchmarkRunner.Run<{{Signature}}Bench>();

        [MemoryDiagnoser]
        public class {{Signature}}Bench
        {
            // TODO: arrange Predict<{{Signature}}> + FakeChatClient and call RunAsync here.
            [Benchmark]
            public int Noop() => 1;
        }
        """;

    private const string CsprojTemplate = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="BenchmarkDotNet" Version="0.14.0" />
          </ItemGroup>
        </Project>
        """;

    public static void Write(string signature, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var prog = ProgramTemplate.Replace("{{Signature}}", signature, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(outputDir, "Program.cs"), prog);
        File.WriteAllText(
            Path.Combine(outputDir, $"{signature}.Benchmarks.csproj"),
            CsprojTemplate);
    }
}
