using System.Collections.Generic;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum.Agents.SpeakerSelectors;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Agents.SpeakerSelectors;

public class LlmDrivenSelectorTests
{
    sealed class StubAgent(string name, string role) : IAgent
    {
        public string Name { get; } = name;
        public string Role { get; } = role;
        public ValueTask<AgentMessage> ReplyAsync(ConversationContext ctx, System.Threading.CancellationToken ct) =>
            ValueTask.FromResult(new AgentMessage(Name, Role, ""));
    }

    sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, System.Exception? exception, System.Func<TState, System.Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    static IReadOnlyList<IAgent> Agents() =>
    [
        new StubAgent("planner", "planner"),
        new StubAgent("coder", "coder"),
        new StubAgent("critic", "critic"),
    ];

    [Fact]
    public async Task Picks_named_agent_from_llm_response()
    {
        var chat = new FakeChatClient().EnqueueText("[[ ## NextSpeaker ## ]]\ncoder");
        var sel = new LlmDrivenSelector(chat);
        var ctx = new ConversationContext(
            [new AgentMessage("user", "user", "kick off")],
            memory: null,
            turnIndex: 0);
        var pick = await sel.SelectAsync(ctx, Agents(), TestContext.Current.CancellationToken);
        pick.Name.ShouldBe("coder");
    }

    [Fact]
    public async Task Falls_back_to_round_robin_on_unknown_name_and_warns()
    {
        var chat = new FakeChatClient().EnqueueText("[[ ## NextSpeaker ## ]]\nunknown_agent");
        var logger = new CapturingLogger<LlmDrivenSelector>();
        var sel = new LlmDrivenSelector(chat, logger);
        var ctx = new ConversationContext(
            [new AgentMessage("user", "user", "go")],
            memory: null,
            turnIndex: 1);
        var pick = await sel.SelectAsync(ctx, Agents(), TestContext.Current.CancellationToken);
        pick.Name.ShouldBe("coder");  // turn index 1 with round-robin -> coder
        logger.Warnings.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Case_insensitive_name_match()
    {
        var chat = new FakeChatClient().EnqueueText("[[ ## NextSpeaker ## ]]\nCRITIC");
        var sel = new LlmDrivenSelector(chat, NullLogger<LlmDrivenSelector>.Instance);
        var ctx = new ConversationContext([], memory: null, turnIndex: 0);
        var pick = await sel.SelectAsync(ctx, Agents(), TestContext.Current.CancellationToken);
        pick.Name.ShouldBe("critic");
    }

    [Fact]
    public async Task Empty_agents_throws_argument_exception()
    {
        var chat = new FakeChatClient();
        var sel = new LlmDrivenSelector(chat);
        var ctx = new ConversationContext([], memory: null, turnIndex: 0);
        await Should.ThrowAsync<System.ArgumentException>(async () =>
            await sel.SelectAsync(ctx, [], TestContext.Current.CancellationToken));
    }
}
