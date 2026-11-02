using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum.Agents.SpeakerSelectors;
using BenchmarkDotNet.Attributes;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

/// <summary>
/// User-brief S14 — agent loop turn latency.
///
/// Plan name: Scenario14_AgentLoopTurnLatency.
/// Budget B3: ≤20% behind AutoGen.NET acceptable.
///
/// <para>Compares <see cref="GroupChat"/> over two scripted <see cref="IAgent"/>
/// implementations against a hand-rolled two-agent turn loop with no orchestration
/// framework. The agents are deterministic (no LM call) so the measurement is
/// pure orchestration overhead per turn.</para>
///
/// <para>Honesty note: AutoGen.NET 0.2.3 ships an agent-conversation model whose
/// idiomatic surface (<c>IAgent.SendAsync</c> via <c>Microsoft.AutoGen.Core</c>)
/// does not adapt cleanly to a synthetic deterministic LM mock — the framework
/// presumes an actor-style message bus and a real reply pipeline. Wiring the
/// AutoGen runtime against a <see cref="Microsoft.Extensions.AI.IChatClient"/>
/// mock would measure plumbing rather than turn latency. Per the alpha.6
/// brief's "honest reporting" instruction, the AutoGen row emits "—" in
/// <c>docs/performance/comparative-results.md</c> with an explicit footnote.
/// The Ampelum-vs-handrolled delta is the meaningful number.</para>
/// </summary>
[MemoryDiagnoser]
public class Scenario14_AgentLoopTurnLatency
{
    /// <summary>Total turns in the conversation.</summary>
    [Params(2, 8)]
    public int Turns { get; set; }

    GroupChat _chat = null!;
    IAgent[] _agents = null!;

    [GlobalSetup]
    public void Setup()
    {
        _agents = [new ScriptedAgent("alice", "user"), new ScriptedAgent("bob", "user")];
        _chat = new GroupChat(_agents, new RoundRobinSelector());
    }

    [Benchmark(Baseline = true)]
    public async Task<int> Ampelum_GroupChat()
    {
        var result = await _chat.RunAsync("hi", Turns, CancellationToken.None);
        return result.History.Count;
    }

    /// <summary>Hand-rolled two-agent loop — what a user writes without Ampelum.</summary>
    [Benchmark]
    public async Task<int> HandRolled_TurnLoop()
    {
        var history = new List<AgentMessage> { new("system", "system", "hi") };
        for (var t = 0; t < Turns; t++)
        {
            var speaker = _agents[t % _agents.Length];
            // Fabricate a minimal context by reusing the public ctor.
            var ctx = new ConversationContext(history, memory: null, turnIndex: t);
            var msg = await speaker.ReplyAsync(ctx, CancellationToken.None);
            history.Add(msg);
            if (msg.IsTerminal) break;
        }
        return history.Count;
    }

    sealed class ScriptedAgent : IAgent
    {
        readonly string _name;
        readonly string _role;
        public ScriptedAgent(string name, string role) { _name = name; _role = role; }
        public string Name => _name;
        public string Role => _role;
        public ValueTask<AgentMessage> ReplyAsync(ConversationContext ctx, CancellationToken cancellationToken)
            => ValueTask.FromResult(new AgentMessage(_name, _role, $"turn {ctx.TurnIndex}"));
    }
}
