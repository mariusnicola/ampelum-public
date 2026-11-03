using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Safety.Tools;

namespace Ampelum.Samples.SafetyGateway;

/// <summary>Everything about a gateway run that is neither a key nor a model id.</summary>
public sealed record GatewayOptions
{
    /// <summary>Stable name of the assistant; trust is tracked per agent type and domain.</summary>
    public required string AgentType { get; init; }

    /// <summary>Area the trust level applies to.</summary>
    public required string Domain { get; init; }

    /// <summary>Identity scope claims, for example <c>tools:information</c>.</summary>
    public required IReadOnlyList<string> Scope { get; init; }

    /// <summary>Mode the tool policy runs in: talk, build or review.</summary>
    public required string Mode { get; init; }

    public required TimeSpan RunDuration { get; init; }

    /// <summary>File the audit trail is appended to.</summary>
    public required string AuditPath { get; init; }

    /// <summary>Write audit entries from a background queue; entries are on disk after the gateway is disposed.</summary>
    public bool BatchAudit { get; init; }

    /// <summary>Alias of the moderation API key in the secret store.</summary>
    public required string ModerationKeyAlias { get; init; }

    /// <summary>Alias of the directory credential in the secret store.</summary>
    public required string DirectoryCredentialAlias { get; init; }

    /// <summary>Moderation endpoint, or null for the moderator's own default.</summary>
    public Uri? ModerationEndpoint { get; init; }

    /// <summary>Per-token prices used for the cost estimate.</summary>
    public required ModelRateTable Rates { get; init; }

    public int MaxToolIterations { get; init; } = 4;

    /// <summary>The helpdesk assistant: read-only lookups plus file writes, in talk mode.</summary>
    public static GatewayOptions Helpdesk(string auditPath, ModelRateTable rates, Uri? moderationEndpoint = null) => new()
    {
        AgentType = "helpdesk-assistant",
        Domain = "helpdesk",
        Scope = ["tools:information", "tools:data-access", "tools:data-mutation"],
        Mode = "talk",
        RunDuration = TimeSpan.FromMinutes(30),
        AuditPath = auditPath,
        ModerationKeyAlias = "moderation_key",
        DirectoryCredentialAlias = "directory_token",
        ModerationEndpoint = moderationEndpoint,
        Rates = rates,
    };
}

/// <summary>Turns identity scope claims into the tool categories the gate allows.</summary>
public static class ScopePolicy
{
    const string Prefix = "tools:";

    public static ToolGatePolicy FromScope(IEnumerable<string> scope)
    {
        var allowed = new HashSet<ToolCategory>();
        foreach (var claim in scope)
        {
            if (!claim.StartsWith(Prefix, StringComparison.Ordinal))
                continue;
            var name = claim[Prefix.Length..].Replace("-", "", StringComparison.Ordinal);
            if (Enum.TryParse<ToolCategory>(name, ignoreCase: true, out var category))
                allowed.Add(category);
        }
        return new ToolGatePolicy(AllowCategories: allowed);
    }
}
