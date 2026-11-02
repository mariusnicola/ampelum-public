using SignalGrid.Ampelum.Audit;

namespace Ampelum.Samples.SupportDesk;

public enum TicketCategory { Billing, Shipping, Technical, Other }

public enum TicketUrgency { Low, Normal, High }

public enum TicketSentiment { Negative, Neutral, Positive }

public sealed record SupportTicket(string Id, string Text);

public sealed record Triage(TicketCategory Category, TicketUrgency Urgency, TicketSentiment Sentiment);

public sealed record Order(string Id, string Status, string Carrier, string Eta);

/// <summary>Everything one handled ticket produced, including proof that the audit chain holds.</summary>
public sealed record SupportDeskResult(
    SupportTicket Ticket,
    Triage Triage,
    string KnowledgeAnswer,
    IReadOnlyList<string> KnowledgeSources,
    string OrderStatus,
    string Reply,
    int AuditEntries,
    AuditChainVerificationResult AuditChain);

/// <summary>Tunable limits of the pipeline; the model and key are never part of it.</summary>
public sealed class SupportDeskOptions
{
    public int KnowledgeTopK { get; init; } = 2;
    public int MaxToolIterations { get; init; } = 4;
    public int ReplyRetries { get; init; } = 2;
}
