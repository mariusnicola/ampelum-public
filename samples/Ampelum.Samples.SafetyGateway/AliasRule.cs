namespace Ampelum.Samples.SafetyGateway;

/// <summary>Which secret aliases a tool may name. The run and the preflight review both use it, so they agree.</summary>
public static class AliasRule
{
    public const string Reason = "That credential alias is not allowed for this tool.";

    public static bool Refuses(IReadOnlyDictionary<string, string[]> allowed, string tool, string? alias) =>
        !(allowed.GetValueOrDefault(tool) ?? []).Contains(alias);
}
