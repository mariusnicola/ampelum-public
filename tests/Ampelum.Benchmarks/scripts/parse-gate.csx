// Run with: dotnet script parse-gate.csx -- <BenchmarkDotNet.Artifacts/results> <gate-thresholds.json>
#r "nuget: System.Text.Json, 8.0.0"
using System;
using System.IO;
using System.Linq;
using System.Text.Json;

if (Args.Count < 2)
{
    Console.Error.WriteLine("usage: parse-gate.csx <results-dir> <thresholds-json>");
    Environment.Exit(2);
    return;
}

var resultsDir = Args[0];
var thresholdsPath = Args[1];

var thresholds = JsonDocument.Parse(File.ReadAllText(thresholdsPath))
    .RootElement.GetProperty("gates")
    .EnumerateArray()
    .Select(g => new
    {
        Benchmark = g.GetProperty("benchmark").GetString()!,
        Metric    = g.GetProperty("metric").GetString()!,
        Max       = g.GetProperty("max").GetDouble(),
        Baseline  = g.TryGetProperty("baseline", out var b) ? b.GetString() : null
    })
    .ToList();

var reports = Directory.EnumerateFiles(resultsDir, "*-report-full.json").ToList();
if (reports.Count == 0)
{
    Console.Error.WriteLine($"no BenchmarkDotNet reports found under {resultsDir}");
    Environment.Exit(2);
    return;
}

var allBenches = reports
    .SelectMany(p => JsonDocument.Parse(File.ReadAllText(p)).RootElement.GetProperty("Benchmarks").EnumerateArray())
    .Select(b => new
    {
        DisplayInfo  = b.GetProperty("DisplayInfo").GetString()!,
        FullName     = b.GetProperty("FullName").GetString()!,
        MeanNs       = b.GetProperty("Statistics").GetProperty("Mean").GetDouble(),
        AllocBytes   = b.GetProperty("Memory").TryGetProperty("BytesAllocatedPerOperation", out var ba) ? ba.GetDouble() : 0.0
    })
    .ToList();

double Lookup(string name) =>
    allBenches.FirstOrDefault(b => b.FullName.EndsWith(name, StringComparison.Ordinal))?.MeanNs
    ?? throw new InvalidOperationException($"benchmark '{name}' not found in reports");

var failures = 0;
foreach (var g in thresholds)
{
    var match = allBenches.FirstOrDefault(b => b.FullName.EndsWith(g.Benchmark, StringComparison.Ordinal));
    if (match is null)
    {
        Console.WriteLine($"SKIP {g.Benchmark} (not present)");
        continue;
    }

    double observed;
    string unit;
    switch (g.Metric)
    {
        case "MeanNs":
            observed = match.MeanNs; unit = "ns";
            break;
        case "AllocBytes":
            observed = match.AllocBytes; unit = "B";
            break;
        case "MeanOverheadNs":
            observed = match.MeanNs - Lookup(g.Baseline!); unit = "ns";
            break;
        default:
            Console.Error.WriteLine($"unknown metric '{g.Metric}' for {g.Benchmark}");
            failures++; continue;
    }

    var ok = observed <= g.Max;
    var verdict = ok ? "PASS" : "FAIL";
    Console.WriteLine($"{verdict} {g.Benchmark} {g.Metric}={observed:F2}{unit} (max={g.Max}{unit})");
    if (!ok) failures++;
}

if (failures > 0)
{
    Console.Error.WriteLine($"{failures} gate(s) breached.");
    Environment.Exit(1);
}
