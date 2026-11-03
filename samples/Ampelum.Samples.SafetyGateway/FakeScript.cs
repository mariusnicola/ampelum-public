using System.Text.Json;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Testing;

namespace Ampelum.Samples.SafetyGateway;

/// <summary>Scripted model answers, in the order the gateway asks for them.</summary>
public static class FakeScript
{
    public const string LeaveAnswer = "Employees get 25 days of paid leave per year.";
    public const string ToolAnswer = "Dana Reyes is in Payroll. Saving the note and running the command were refused.";

    /// <summary>Alias the model is told to pass for the directory credential.</summary>
    public const string CredentialAlias = "directory_token";

    /// <summary>The question, the tool run and a blocked reply that --fake walks through.</summary>
    public static FakeChatClient Demo()
    {
        var chat = new FakeChatClient();
        EnqueueAnswer(chat, LeaveAnswer, inputTokens: 120, outputTokens: 18);
        EnqueueToolTurn(chat,
            ("call-1", ToolCatalog.Search, new Dictionary<string, object?> { ["query"] = "Payroll", ["credential"] = CredentialAlias }),
            ("call-2", ToolCatalog.WriteFile, new Dictionary<string, object?> { ["name"] = "notes", ["content"] = "Payroll contact" }),
            ("call-3", ToolCatalog.RunShell, new Dictionary<string, object?> { ["command"] = "df -h" }));
        EnqueueFinal(chat, ToolAnswer, inputTokens: 260, outputTokens: 30);
        EnqueueAnswer(chat, $"Here is an answer. {FakeData.UnsafeMarker}", inputTokens: 90, outputTokens: 12);
        return chat;
    }

    public static void EnqueueAnswer(FakeChatClient chat, string answer, int inputTokens, int outputTokens) =>
        chat.EnqueueText(JsonSerializer.Serialize(new { answer }), Usage(inputTokens, outputTokens));

    /// <summary>One assistant turn that asks for several tools at once.</summary>
    public static void EnqueueToolTurn(
        FakeChatClient chat, params (string CallId, string Tool, Dictionary<string, object?> Args)[] calls)
    {
        var turn = new ChatMessage(ChatRole.Assistant, "");
        foreach (var (callId, tool, args) in calls)
            turn.Contents.Add(new FunctionCallContent(callId, tool, args));
        chat.EnqueueRaw(new ChatResponse(turn) { Usage = Usage(100, 20) });
    }

    public static void EnqueueFinal(FakeChatClient chat, string answer, int inputTokens, int outputTokens) =>
        chat.EnqueueText($"Thought: I have what I need.\nfinal_answer: {answer}", Usage(inputTokens, outputTokens));

    static UsageDetails Usage(int inputTokens, int outputTokens) =>
        new() { InputTokenCount = inputTokens, OutputTokenCount = outputTokens };
}
