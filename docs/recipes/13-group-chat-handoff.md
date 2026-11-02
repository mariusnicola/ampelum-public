# Group Chat with Handoff

`GroupChat` orchestrates two-or-more `IAgent`s with a pluggable
`ISpeakerSelector`. An emitted `AgentMessage` may set `Handoff` to
override the selector for the next turn, and `IsTerminal` to terminate
the conversation.

```csharp
// ampelum:recipe group-chat-handoff
var researcher = new RecipeScriptedAgent("Researcher", "research", new[]
{
    new AgentMessage("Researcher", "research", "research-1") { Handoff = "Critic" },
});
var critic = new RecipeScriptedAgent("Critic", "critic", new[]
{
    new AgentMessage("Critic", "critic", "critic-1") { IsTerminal = true },
});

var chat = new GroupChat(
    new IAgent[] { researcher, critic },
    new RoundRobinSelector());

var result = await chat.RunAsync("Solve X.", maxTurns: 6, ct);
result.Reason.ShouldBe(TerminationReason.TerminalMessage);
result.History.Count.ShouldBe(3);
result.History[1].SenderName.ShouldBe("Researcher");
result.History[1].Handoff.ShouldBe("Critic");
result.History[2].SenderName.ShouldBe("Critic");
result.History[2].IsTerminal.ShouldBeTrue();
```

History rows: 0=seed system prompt, 1=Researcher (handoff to Critic),
2=Critic (terminal). Round-robin would have picked Critic anyway here;
swap the order or add a third agent to see the override path defeat the
selector.
