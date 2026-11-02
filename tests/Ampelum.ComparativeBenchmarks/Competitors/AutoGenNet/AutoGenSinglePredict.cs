using AutoGen.Core;
using Microsoft.Extensions.AI;

namespace Ampelum.ComparativeBenchmarks.Competitors.AutoGenNet;

/// <summary>
/// Real AutoGen.NET single-Predict harness — replaces the alpha.4 placeholder
/// (direct chat call byte-identical to <c>MeaiRaw</c>).
/// <para>
/// AutoGen 0.2.3 (LittleLittleCloud community fork) does not ship a
/// first-party MEAI <see cref="IChatClient"/> connector — its built-in
/// <c>AssistantAgent</c> is wired through <c>ConversableAgentConfig</c> to
/// OpenAI / Azure OpenAI directly. To compare AutoGen orchestration overhead
/// against the same byte-identical mock the rest of the suite uses, we wrap
/// the supplied <see cref="IChatClient"/> in a custom <see cref="IAgent"/>
/// (<see cref="ChatClientAgent"/>) and then route it through AutoGen's
/// canonical <see cref="MiddlewareAgent"/> wrapper. This measures the real
/// AutoGen agent + middleware dispatch path; the only "shim" is the inner
/// IAgent, which is the minimum surface area needed to share the mock.
/// </para>
/// </summary>
public static class AutoGenSinglePredict
{
    public static MiddlewareAgent BuildAgent(IChatClient chat)
    {
        var inner = new ChatClientAgent("ampelum-bench-mock", chat);
        // Empty middleware pipeline — measures the MiddlewareAgent dispatch
        // shell only. Scenarios that want to add real middlewares can call
        // BuildAgentWithMiddlewares.
        return new MiddlewareAgent(inner, "ampelum-bench-assistant");
    }

    public static async Task<string> RunAsync(MiddlewareAgent agent, string question)
    {
        var reply = await agent.GenerateReplyAsync([new TextMessage(Role.User, question)]);
        return reply is TextMessage tm ? tm.Content : (reply.GetContent() ?? string.Empty);
    }

    /// <summary>Minimal <see cref="IAgent"/> that delegates to a MEAI <see cref="IChatClient"/>.</summary>
    private sealed class ChatClientAgent : IAgent
    {
        private readonly IChatClient _chat;
        public ChatClientAgent(string name, IChatClient chat)
        {
            Name = name;
            _chat = chat;
        }
        public string Name { get; }
        public async Task<IMessage> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var meaiMessages = messages.Select(ToMeaiMessage).ToList();
            var resp = await _chat.GetResponseAsync(meaiMessages, options: null, cancellationToken)
                .ConfigureAwait(false);
            return new TextMessage(Role.Assistant, resp.Text ?? string.Empty, from: Name);
        }

        private static ChatMessage ToMeaiMessage(IMessage m)
        {
            var role = m.GetRole() switch
            {
                Role r when r == Role.User => ChatRole.User,
                Role r when r == Role.System => ChatRole.System,
                _ => ChatRole.Assistant,
            };
            return new ChatMessage(role, m.GetContent() ?? string.Empty);
        }
    }
}
