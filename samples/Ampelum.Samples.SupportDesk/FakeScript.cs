using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Testing;

namespace Ampelum.Samples.SupportDesk;

/// <summary>
/// Scripted model for fake mode. Each step is built when its call arrives, so a step can read the
/// messages it was sent. Calls are recorded by <see cref="Inner"/>.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    readonly Queue<Func<IReadOnlyList<ChatMessage>, ChatResponse>> _steps = new();

    public FakeChatClient Inner { get; } = new();

    public ScriptedChatClient Enqueue(Func<IReadOnlyList<ChatMessage>, ChatResponse> step)
    {
        _steps.Enqueue(step);
        return this;
    }

    public ScriptedChatClient EnqueueText(string text) =>
        Enqueue(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sent = messages.ToArray();
        if (!_steps.TryDequeue(out var step))
            throw new InvalidOperationException("ScriptedChatClient: no scripted step left.");
        Inner.EnqueueRaw(step(sent));
        return Inner.GetResponseAsync(sent, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The support desk does not stream.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => Inner.Dispose();
}

/// <summary>Scripted model answers for one ticket, in the order the pipeline asks for them.</summary>
public static class FakeScript
{
    const string NotFoundPrefix = "No order with id";

    public static ScriptedChatClient HappyPath(ScriptedChatClient? client = null)
    {
        var chat = client ?? new ScriptedChatClient();
        EnqueueTriage(chat);
        EnqueueKnowledge(chat);
        EnqueueOrderLookup(chat, "A-1001");
        EnqueueReplyFromOrderStatus(chat);
        return chat;
    }

    public static void EnqueueTriage(ScriptedChatClient chat) =>
        chat.EnqueueText("""{"category":"Shipping","urgency":"High","sentiment":"Negative"}""");

    public static void EnqueueKnowledge(ScriptedChatClient chat) =>
        chat.EnqueueText("Standard delivery takes 3 to 5 business days; the tracking link in the shipping email shows where the parcel is [doc1].");

    /// <summary>Two model turns: the tool call, then an answer built from the tool result the model was sent.</summary>
    public static void EnqueueOrderLookup(ScriptedChatClient chat, string orderId)
    {
        chat.Enqueue(_ =>
        {
            var toolTurn = new ChatMessage(ChatRole.Assistant, "");
            toolTurn.Contents.Add(new FunctionCallContent("call-1", "lookup_order",
                new Dictionary<string, object?> { ["orderId"] = orderId }));
            return new ChatResponse(toolTurn);
        });
        chat.Enqueue(messages =>
        {
            var toolText = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
                .Last().Result?.ToString() ?? "";
            var answer = toolText.StartsWith(NotFoundPrefix, StringComparison.Ordinal)
                ? $"Order not found. {toolText}"
                : toolText;
            return Text($"Thought: I have the result.\nfinal_answer: {answer}");
        });
    }

    /// <summary>The reply follows the order status the model was sent, so a missing order is not reported as shipped.</summary>
    public static void EnqueueReplyFromOrderStatus(ScriptedChatClient chat) =>
        chat.Enqueue(messages =>
        {
            var prompt = string.Join("\n", messages.Select(m => m.Text));
            var reply = prompt.Contains("Order not found", StringComparison.Ordinal)
                ? "Sorry, we could not find that order. Please check the order number and reply with it."
                : prompt.Contains(": Shipped,", StringComparison.Ordinal)
                    ? "Sorry for the wait. Your order has shipped with NorthPost and should arrive in 2 business days. You can follow it with the tracking link in your shipping email."
                    : prompt.Contains(": Processing,", StringComparison.Ordinal)
                        ? "Thanks for your patience. Your order is still processing and has not left the warehouse yet. We will email you the tracking link once it ships."
                        : "Thanks for your message. We are checking your order and will update you soon.";
            return Text(JsonSerializer.Serialize(new { reply }));
        });

    /// <summary>A null reply is what the schema critic rejects.</summary>
    public static void EnqueueReply(ScriptedChatClient chat, string? reply) =>
        chat.EnqueueText(reply is null ? """{"reply":null}""" : JsonSerializer.Serialize(new { reply }));

    static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));
}
