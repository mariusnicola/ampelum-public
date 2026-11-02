using System.Globalization;
using SignalGrid.Ampelum.Testing;

namespace Ampelum.Samples.ExpenseApproval;

/// <summary>Scripted chat client for runs without a live model.</summary>
public static class FakeExpenseClient
{
    /// <summary>Creates a client whose replies are the given extractions, written as the adapter's response markers.</summary>
    public static FakeChatClient Create(params Expense[] replies)
    {
        var client = new FakeChatClient();
        foreach (var reply in replies)
            client.EnqueueText(Reply(reply.Vendor, reply.Amount.ToString(CultureInfo.InvariantCulture), reply.Category));
        return client;
    }

    /// <summary>Creates a client whose next reply carries the amount text exactly as given, valid or not.</summary>
    public static FakeChatClient Create(string vendor, string rawAmount, string category) =>
        new FakeChatClient().EnqueueText(Reply(vendor, rawAmount, category));

    static string Reply(string vendor, string rawAmount, string category) =>
        $"[[ ## Vendor ## ]]\n{vendor}\n\n[[ ## Amount ## ]]\n{rawAmount}\n\n[[ ## Category ## ]]\n{category}";
}
