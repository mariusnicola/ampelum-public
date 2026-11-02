using LangChain.Chains;
using LangChain.Chains.HelperChains;
using LangChain.Providers;
using Microsoft.Extensions.AI;
using LcChatRequest = LangChain.Providers.ChatRequest;
using LcChatResponse = LangChain.Providers.ChatResponse;
using LcChatSettings = LangChain.Providers.ChatSettings;
using LcMessage = LangChain.Providers.Message;
using LcMessageRole = LangChain.Providers.MessageRole;
using LcUsage = LangChain.Providers.Usage;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Ampelum.ComparativeBenchmarks.Competitors.LangChainNet;

/// <summary>
/// Real LangChain.NET (tryAGI 0.17.0) single-Predict harness — replaces the
/// alpha.4 placeholder (direct chat call byte-identical to <c>MeaiRaw</c>).
/// <para>
/// LangChain.NET's chat surface is <see cref="IChatModel"/> + <see cref="ChatRequest"/>,
/// not MEAI <see cref="IChatClient"/>. The minimum honest adapter is a
/// custom <see cref="ChatModel"/> subclass (<see cref="ChatClientChatModel"/>)
/// that delegates to the supplied <see cref="IChatClient"/>; everything else
/// — <see cref="LLMChain"/> construction, <c>RunAsync</c> dispatch, prompt
/// formatting through <see cref="ChainValues"/> — is real LangChain code.
/// </para>
/// </summary>
public static class LangChainSinglePredict
{
    /// <summary>Build a real LangChain pipeline: Set(question) -> Template -> LLM.
    /// This is the idiomatic <c>Chain.*</c> fluent shape from LangChain.NET 0.17.
    /// We return the holder so the bench can rebuild the per-call chain (Chain
    /// pipelines are mutable when re-run).</summary>
    public static LangChainHarness BuildChain(IChatClient chat)
        => new(new ChatClientChatModel("ampelum-bench-mock", chat));

    public static async Task<string> RunAsync(LangChainHarness h, string question)
    {
        // Build the real Chain.Set | Chain.Template | Chain.LLM stack.
        // Chain.Set seeds an input value; Chain.Template renders {question} ->
        // a prompt; Chain.LLM dispatches the prompt through the IChatModel.
        var chain = Chain.Set(question, outputKey: "question")
                  | Chain.Template("Answer: {question}", outputKey: "prompt")
                  | Chain.LLM(h.Model, inputKey: "prompt", outputKey: "text");
        var result = await chain.RunAsync("text");
        return result ?? string.Empty;
    }

    /// <summary>Holder that owns the <see cref="ChatClientChatModel"/> across iterations.</summary>
    public sealed class LangChainHarness
    {
        public LangChainHarness(IChatModel model) => Model = model;
        public IChatModel Model { get; }
    }

    /// <summary>Adapter <see cref="ChatModel"/> that routes
    /// <see cref="GenerateAsync"/> through a MEAI <see cref="IChatClient"/>.</summary>
    private sealed class ChatClientChatModel : ChatModel
    {
        private readonly IChatClient _chat;
        public ChatClientChatModel(string id, IChatClient chat) : base(id)
        {
            _chat = chat;
            ContextLength = 4096;
        }

        public override async IAsyncEnumerable<LcChatResponse> GenerateAsync(
            LcChatRequest request,
            LcChatSettings? settings = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var meaiMessages = new List<MeaiChatMessage>(request.Messages.Count);
            foreach (var m in request.Messages)
                meaiMessages.Add(new MeaiChatMessage(ToMeaiRole(m.Role), m.Content));
            var resp = await _chat.GetResponseAsync(meaiMessages, options: null, cancellationToken)
                .ConfigureAwait(false);
            var text = resp.Text ?? string.Empty;
            yield return new LcChatResponse
            {
                Messages = [.. request.Messages, new LcMessage(text, LcMessageRole.Ai)],
                UsedSettings = settings ?? LcChatSettings.Default,
                Usage = LcUsage.Empty,
            };
        }

        private static ChatRole ToMeaiRole(LcMessageRole role) => role switch
        {
            LcMessageRole.System => ChatRole.System,
            LcMessageRole.Ai => ChatRole.Assistant,
            LcMessageRole.ToolResult => ChatRole.Tool,
            _ => ChatRole.User,
        };
    }
}
