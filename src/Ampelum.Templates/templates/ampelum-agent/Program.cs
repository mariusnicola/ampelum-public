using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum.Agents.Selection;
using Company.Agent.Agents;
using Company.Agent.Tools;

// Two-agent GroupChat: Researcher drafts, Critic approves. The CalculatorTool
// is registered as a typed tool the Researcher can use.

var researcher = new Researcher();
var critic = new Critic();
var tool = new CalculatorTool();
_ = tool; // Tool is wired into the registry in a real app — see README.

var groupChat = new GroupChat(
    agents: [researcher, critic],
    selector: new RoundRobinSelector());

var result = await groupChat.RunAsync(
    initialPrompt: "Summarise the impact of agentic AI in two sentences.",
    maxTurns: 4,
    cancellationToken: default);

foreach (var message in result.History)
{
    Console.WriteLine($"[{message.Speaker}] {message.Content}");
}
Console.WriteLine($"Termination: {result.Reason}");
