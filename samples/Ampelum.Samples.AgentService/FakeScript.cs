using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Testing;

namespace Ampelum.Samples.AgentService;

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
        throw new NotSupportedException("The scripted model does not stream.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => Inner.Dispose();
}

/// <summary>Scripted model answers for each part of the sample.</summary>
public static class FakeScript
{
    public const string ServiceAnswer = "Standard delivery takes 3 to 5 business days.";

    /// <summary>One answer for the service module, in the wire format the module's adapter parses.</summary>
    public static FakeChatClient ServiceModel(string answer = ServiceAnswer) =>
        new FakeChatClient().EnqueueOutputs(new AnswerSignature { Question = "", Answer = answer });

    /// <summary>Two model turns: the tool call, then an answer built from the tool result the model was sent.</summary>
    public static ScriptedChatClient ToolModel(string orderId)
    {
        var chat = new ScriptedChatClient();
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
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Thought: I have the result.\nfinal_answer: {toolText}"));
        });
        return chat;
    }

    /// <summary>One answer for the REPL session.</summary>
    public static FakeChatClient ReplModel(string answer = "Order A-1001 has shipped.") =>
        new FakeChatClient().EnqueueText(answer);
}
