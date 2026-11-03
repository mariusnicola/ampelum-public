using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Execution;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>What the fix run did and whether the edited file passes the sample's own check.</summary>
public sealed record FixOutcome(SubAgentResult Run, bool Passed, string Detail);

/// <summary>
/// After the report is posted, a coding sub-agent fixes issue #102 in the extracted copy of the demo project. It uses
/// the runner's file tools and ends with DONE:. The sample then reads the edited file instead of trusting the claim.
/// </summary>
public static partial class IssueFixer
{
    /// <summary>Turns the runner may use before it gives up.</summary>
    public const int MaxTurns = 6;

    /// <summary>The file the fix is expected to change, relative to the project root.</summary>
    public const string PricingFile = "src/Pricing.cs";

    /// <summary>What the scripted sub-agent writes. A live model writes its own.</summary>
    public const string RoundedPricing = """
namespace Shop;

public sealed class Pricing
{
    public decimal ApplyDiscount(decimal amount, decimal percent)
    {
        return Math.Round(amount - amount * percent / 100m, 2, MidpointRounding.AwayFromZero);
    }
}
""";

    const string Description =
        "Fix issue #102: Pricing.ApplyDiscount never rounds. Edit src/Pricing.cs so the discounted amount is rounded to two decimals. "
        + "Use read_file to look at the file and write_file to save the whole file back. Touch no other file.";

    /// <summary>
    /// The runner offers tools but never runs the calls a model makes; the chat client has to do that. This wraps the
    /// client so the calls reach the runner's tools.
    /// </summary>
    public static IChatClient WithToolInvocation(IChatClient inner) => new FunctionInvokingChatClient(inner);

    /// <summary>Runs the sub-agent in <paramref name="root"/> and checks the file it was asked to change.</summary>
    public static async Task<FixOutcome> FixAsync(string root, IChatClient client, int maxTurns = MaxTurns, CancellationToken ct = default, SessionBudgetMeter? budget = null)
    {
        var target = Path.Combine(root, PricingFile.Replace('/', Path.DirectorySeparatorChar));
        var before = File.ReadAllText(target);

        var runner = new SubAgentRunner(client, root, Guid.NewGuid().ToString("N"), maxTurns, budget);
        var run = await runner.RunAsync(new SubAgentTask { Id = "fix-102", Description = Description }, ct).ConfigureAwait(false);

        var (passed, detail) = Check(run, target, before);
        return new FixOutcome(run, passed, detail);
    }

    static (bool Passed, string Detail) Check(SubAgentResult run, string target, string before)
    {
        if (!run.Success)
            return (false, $"the sub-agent did not end on DONE: {run.Outcome}");
        // The runner records the path as the model spelled it, so slashes may differ from the path built here.
        if (!run.FilesChanged.Select(Path.GetFullPath).Contains(Path.GetFullPath(target), StringComparer.OrdinalIgnoreCase))
            return (false, "the runner did not record a write to src/Pricing.cs");
        var after = File.ReadAllText(target);
        if (after == before)
            return (false, "src/Pricing.cs is unchanged");
        if (!after.Contains("ApplyDiscount", StringComparison.Ordinal))
            return (false, "src/Pricing.cs no longer declares ApplyDiscount");
        var method = after[after.IndexOf("ApplyDiscount", StringComparison.Ordinal)..];
        if (!ReturnsRounded().IsMatch(method))
            return (false, "ApplyDiscount does not return a Math.Round(...) call");
        return (true, "ApplyDiscount returns a Math.Round(...) call (text check, the file was not compiled)");
    }

    [GeneratedRegex(@"return\s+Math\.Round\(")]
    private static partial Regex ReturnsRounded();
}
