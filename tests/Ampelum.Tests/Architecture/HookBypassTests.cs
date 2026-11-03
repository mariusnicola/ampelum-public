using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Tests.Architecture;

/// <summary>
/// Every model call in the library must run the caller's hook pipeline (budget, audit, PII, injection checks).
/// A direct <c>GetResponseAsync</c> or <c>GetStreamingResponseAsync</c> call is allowed only in the places listed
/// here; any other one fails this test, so a new call site cannot skip the hooks unnoticed.
/// </summary>
public sealed partial class HookBypassTests
{
    // File -> number of direct calls it may hold, and why.
    static readonly Dictionary<string, int> Allowed = new(StringComparer.Ordinal)
    {
        // The hook entry point itself, and the two modules that run the pipeline inline.
        ["src/Ampelum/Hooks/HookedLmCall.cs"] = 2,
        ["src/Ampelum/Predict.cs"] = 2,
        ["src/Ampelum/StreamingChatSession.cs"] = 1,
        // Chat-client decorators: they forward the call they were given; the hooks run in the caller above them.
        ["src/Ampelum/Caching/TokenCachedChatClient.cs"] = 1,
        ["src/Ampelum/RateLimiting/RateLimitedChatClient.cs"] = 2,
        ["src/Ampelum/Resilience/CircuitBreakerChatClient.cs"] = 2,
        ["src/Ampelum/Routing/RoutedChatClient.cs"] = 2,
        // Fallback when no ambient context exists, so there is no hook pipeline to run; with one, they use HookedLmCall.
        ["src/Ampelum/Adapters/TwoStepAdapter.cs"] = 1,
        ["src/Ampelum/Metrics/LLMAsJudgeMetric.cs"] = 1,
        ["src/Ampelum/Retrieval/Patterns/MultiQueryRetriever.cs"] = 1,
        ["src/Ampelum/Retrieval/Patterns/QueryRewriter.cs"] = 1,
        ["src/Ampelum/Retrieval/RagAdvisor.cs"] = 1,
        ["src/Ampelum.Agent/Skills/LlmSkillSelector.cs"] = 1,
    };

    [Fact]
    public void Src_DirectModelCalls_AreOnlyInTheAllowedPlaces()
    {
        var root = RepoRoot();
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                continue;
            // The test-support package and the analyzers are not runtime paths of a program.
            if (relative.StartsWith("src/Ampelum.Testing/", StringComparison.Ordinal) || relative.StartsWith("src/Ampelum.Analyzers/", StringComparison.Ordinal))
                continue;
            var count = DirectCall().Matches(File.ReadAllText(path)).Count;
            if (count > 0) found[relative] = count;
        }

        var unexpected = found.Where(f => !Allowed.TryGetValue(f.Key, out var max) || f.Value > max)
            .Select(f => $"{f.Key}: {f.Value}").ToList();
        unexpected.ShouldBeEmpty("route these through HookedLmCall, or add them to the list with the reason");
        found.Keys.ShouldContain("src/Ampelum/Hooks/HookedLmCall.cs", "the scan found the hook entry point");
    }

    static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SignalGrid.Ampelum.sln")))
                return dir.FullName;
        throw new InvalidOperationException("SignalGrid.Ampelum.sln not found above the test output folder.");
    }

    [GeneratedRegex(@"\.(GetResponseAsync|GetStreamingResponseAsync)\s*\(")]
    private static partial Regex DirectCall();
}
