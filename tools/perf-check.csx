#!/usr/bin/env dotnet-script
// PerfCheck v0.7 — soft-warn perf budget verifier.
// Usage:
//   dotnet script tools/perf-check.csx -- <bdn-results-dir> [budgets.json]
//
// Reads BenchmarkDotNet JSON exports from the given directory + perf-budgets.json,
// matches each benchmark FullName against a budget entry, and warns on mean-µs or
// alloc-bytes breach. PSQ-6: v0.7 is soft-warn (exit 0 always unless mode == "fail").
// v0.9 promotes to hard-fail once baselines exist for every gated method.

#r "nuget: System.Text.Json, 9.0.0"

using System;
using System.IO;
using System.Linq;
using System.Text.Json;

var bdnDir = Args.ElementAtOrDefault(0) ?? "BenchmarkDotNet.Artifacts/results";
var budgetsPath = Args.ElementAtOrDefault(1) ?? "tools/perf-budgets.json";

if (!File.Exists(budgetsPath))
{
    Console.Error.WriteLine($"PerfCheck: budgets file not found: {budgetsPath}");
    Environment.Exit(0);
}

var budgetsJson = JsonDocument.Parse(File.ReadAllText(budgetsPath));
var mode = budgetsJson.RootElement.TryGetProperty("mode", out var modeEl)
    ? modeEl.GetString() ?? "warn"
    : "warn";
if (!budgetsJson.RootElement.TryGetProperty("budgets", out var budgets))
{
    Console.WriteLine("PerfCheck: no budgets configured; nothing to check.");
    Environment.Exit(0);
}

if (!Directory.Exists(bdnDir))
{
    Console.Error.WriteLine($"WARN: PerfCheck results directory missing: {bdnDir}");
    Environment.Exit(0);
}

var anyBreach = false;
var anyMatch = false;
foreach (var report in Directory.EnumerateFiles(bdnDir, "*-report-full.json"))
{
    JsonDocument doc;
    try { doc = JsonDocument.Parse(File.ReadAllText(report)); }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"WARN: PerfCheck could not parse {report}: {ex.Message}");
        continue;
    }
    if (!doc.RootElement.TryGetProperty("Benchmarks", out var benches)) continue;
    foreach (var bench in benches.EnumerateArray())
    {
        var fullName = bench.TryGetProperty("FullName", out var fnEl) ? fnEl.GetString() : null;
        if (fullName is null) continue;
        if (!budgets.TryGetProperty(fullName, out var budget)) continue;

        anyMatch = true;
        if (!bench.TryGetProperty("Statistics", out var stats)) continue;
        var meanNs = stats.GetProperty("Mean").GetDouble();
        var meanUs = meanNs / 1000.0;
        var allocBytes = bench.TryGetProperty("Memory", out var mem)
            && mem.TryGetProperty("BytesAllocatedPerOperation", out var allocEl)
            ? allocEl.GetInt64()
            : 0;

        var p50UsBudget = budget.GetProperty("p50_us").GetDouble();
        var allocBudget = budget.GetProperty("alloc_bytes").GetInt64();

        if (meanUs > p50UsBudget || allocBytes > allocBudget)
        {
            Console.Error.WriteLine(
                $"WARN: PerfCheck breach {fullName} mean={meanUs:F1}us (budget {p50UsBudget}us) alloc={allocBytes}B (budget {allocBudget}B)");
            anyBreach = true;
        }
        else
        {
            Console.WriteLine($"OK: {fullName} mean={meanUs:F1}us alloc={allocBytes}B");
        }
    }
}

if (!anyMatch)
{
    Console.WriteLine("PerfCheck: no benchmark results matched any configured budget.");
}

if (anyBreach && string.Equals(mode, "fail", StringComparison.OrdinalIgnoreCase))
{
    Environment.Exit(1);
}
Environment.Exit(0);
