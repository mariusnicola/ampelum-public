using System.Text.RegularExpressions;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Safety.Policy;

namespace Ampelum.Samples.SupportDesk;

/// <summary>In-code stand-ins for the systems a real desk would query.</summary>
public static class SupportDeskData
{
    public static IReadOnlyList<RetrievedDocument> KnowledgeBase { get; } =
    [
        Doc("kb-refunds", "Refunds: a refund is issued to the original payment method within 5 business days after we receive the returned item."),
        Doc("kb-shipping", "Shipping: standard delivery takes 3 to 5 business days. Use the tracking link in the shipping email to follow a parcel."),
        Doc("kb-password", "Password reset: use the reset link on the sign in page. Support staff never ask for a password."),
        Doc("kb-invoices", "Invoices: invoices are available under Billing in the account menu and can be downloaded as PDF."),
    ];

    public static IReadOnlyDictionary<string, Order> Orders { get; } =
        new Dictionary<string, Order>(StringComparer.OrdinalIgnoreCase)
        {
            ["A-1001"] = new("A-1001", "Shipped", "NorthPost", "2 business days"),
            ["A-1002"] = new("A-1002", "Processing", "none yet", "unknown"),
        };

    public static SupportTicket SampleTicket { get; } =
        new("T-100", "My order A-1001 has not arrived and I am annoyed. How long does shipping take?");

    /// <summary>Replies must never ask the customer for secrets.</summary>
    public static ContentPolicy ReplyPolicy { get; } = new ContentPolicyBuilder()
        .Reject("NO_SECRET_REQUEST",
            new Regex(@"\b(send|share|tell|give)\b[^.]{0,30}\b(password|card number)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled))
        .Build();

    static RetrievedDocument Doc(string id, string text) =>
        new(id, text, 0, new Dictionary<string, object?>());
}
