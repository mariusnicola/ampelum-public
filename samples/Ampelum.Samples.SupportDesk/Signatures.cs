using SignalGrid.Ampelum;

namespace Ampelum.Samples.SupportDesk;

/// <summary>Triage step: the model answers with a JSON object holding three labels.</summary>
[Signature("Classify the support ticket. Category is one of Billing, Shipping, Technical, Other. Urgency is one of Low, Normal, High. Sentiment is one of Negative, Neutral, Positive.")]
public sealed record TriageSignature : ISignature
{
    [Input] public required string TicketText { get; init; }
    [Output] public string Category { get; init; } = "";
    [Output] public string Urgency { get; init; } = "";
    [Output] public string Sentiment { get; init; } = "";
}

/// <summary>Order step: a ReAct loop with one lookup tool, so exactly one output field.</summary>
[Signature("Find the order the ticket refers to with the lookup_order tool and report its status.")]
public sealed record OrderLookupSignature : ISignature
{
    [Input] public required string Question { get; init; }
    [Output] public string Answer { get; init; } = "";
}

/// <summary>Reply step: Hint carries the critic's suggestions into a retry.</summary>
[Signature("Write a short, polite reply to the customer. Use only the facts supplied. Never ask for a password or card number.")]
public sealed record ReplySignature : ISignature
{
    [Input] public required string TicketText { get; init; }
    [Input] public required string Category { get; init; }
    [Input] public required string KnowledgeAnswer { get; init; }
    [Input] public required string OrderStatus { get; init; }
    [Input] public string Hint { get; init; } = "";
    [Output] public string? Reply { get; init; }
}
