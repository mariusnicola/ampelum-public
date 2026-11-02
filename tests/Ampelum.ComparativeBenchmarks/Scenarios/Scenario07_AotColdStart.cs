namespace Ampelum.ComparativeBenchmarks.Scenarios;

/// <summary>
/// User-brief S7 — AOT cold-start.
///
/// Plan name: Scenario07_AotColdStart.
/// Budget D4: ≤30 ms p50 first-Predict on linux-x64 AOT publish.
///
/// <para>Why this is NOT a BDN scenario:</para>
/// BenchmarkDotNet's warm-up + JIT-prime loop fundamentally defeats a
/// cold-start measurement: the thing we want to measure is the
/// process-spawn → first-Predict latency, which BDN explicitly amortises
/// out. alpha.5 therefore ships the cold-start measurement as an outer
/// shell driver — see <c>tools/measure-cold-start.sh</c> — that times the
/// existing <c>Ampelum.AotSmoke</c> binary across N spawns and reports
/// min/p50/p95/max as a one-line JSON record.
///
/// <para>Driver contract:</para>
/// <list type="bullet">
///   <item>Pre-step: <c>dotnet publish tests/Ampelum.AotSmoke -c Release -r linux-x64</c>.</item>
///   <item>Run: <c>tools/measure-cold-start.sh [iterations=10]</c>.</item>
///   <item>Stdout: one JSON line —
///   <c>{"iterations":N,"min_ms":X,"p50_ms":X,"p95_ms":X,"max_ms":X}</c>.</item>
/// </list>
///
/// <para>Authoritative platform:</para>
/// linux-x64 (the AOT smoke project is CI-gated on
/// linux-x64 only; Windows publish can fail under Smart App Control).
/// alpha.6 wires the GitHub workflow that runs this on every PR and
/// fails the build if p50 exceeds the D4 30 ms budget.
///
/// <para>This file ships a typed marker so the comparative-rank tool
/// (alpha.6 <c>tools/comparative-rank.csx</c>) can enumerate every
/// scenario type uniformly via <c>typeof(Scenario07_AotColdStart)</c> and
/// emit a "deferred to driver script" cell rather than a missing row.</para>
/// </summary>
public sealed class Scenario07_AotColdStart
{
    /// <summary>Relative path (from repo root) to the cold-start driver script.</summary>
    public const string DriverScriptPath = "tools/measure-cold-start.sh";

    /// <summary>D4 budget in milliseconds.</summary>
    public const int BudgetP50Ms = 30;

    /// <summary>
    /// Returns a one-line summary the alpha.6 ranker can render verbatim
    /// when the driver hasn't run yet.
    /// </summary>
    public static string Placeholder()
        => $"S7 AOT cold-start: deferred to outer driver '{DriverScriptPath}' (BDN cannot measure cold-start; D4 budget {BudgetP50Ms} ms p50, linux-x64 authoritative).";
}
