using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum.Agents.SpeakerSelectors;
using SignalGrid.Ampelum.Testing;
using SignalGrid.Ampelum;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Agents.IntegrationTests;

[Collection(nameof(ActivityListenerCollection))]
public class GroupChat3AgentIntegrationTests
{
    [Signature("Plan a task in one short sentence.")]
    public sealed record PlanSig : ISignature
    {
        [Input] public required string Goal { get; init; }
        [Output] public string Plan { get; init; } = "";
    }

    [Signature("Write code in one line.")]
    public sealed record CodeSig : ISignature
    {
        [Input] public required string Plan { get; init; }
        [Output] public string Code { get; init; } = "";
    }

    [Signature("Critique the code.")]
    public sealed record CritSig : ISignature
    {
        [Input] public required string Code { get; init; }
        [Output] public string Critique { get; init; } = "";
    }

    sealed class PredictAgent<TSig>(string name, string role, IChatClient chat, System.Func<TSig, AgentMessage> finalise)
        : IAgent where TSig : ISignature
    {
        public string Name { get; } = name;
        public string Role { get; } = role;
        public async ValueTask<AgentMessage> ReplyAsync(ConversationContext ctx, CancellationToken ct)
        {
            // Each agent uses Predict but we don't actually wire the conversation —
            // the FakeChatClient is scripted to return predetermined output for each call.
            // The integration test verifies orchestration glue, not LLM correctness.
            var inputs = SignatureMetadataCache.For<TSig>();
            // Build a degenerate input record by reflection
            var ctor = typeof(TSig).GetConstructors()[0];
            var args = new object?[ctor.GetParameters().Length];
            for (var i = 0; i < args.Length; i++)
                args[i] = ctor.GetParameters()[i].ParameterType == typeof(string) ? "kickoff" : System.Activator.CreateInstance(ctor.GetParameters()[i].ParameterType);
            var sigInstance = (TSig)ctor.Invoke(args);
            var p = Predict.Create<TSig>(chat);
            var pred = await p.ForwardAsync(sigInstance, demos: null, ct: ct);
            return finalise(pred.Value);
        }
    }

    [Fact]
    public async Task Three_agents_run_to_completion_with_handoff()
    {
        var chat = new FakeChatClient()
            // LlmDrivenSelector first pick
            .EnqueueText("[[ ## NextSpeaker ## ]]\nplanner")
            // planner's Predict
            .EnqueueText("[[ ## Plan ## ]]\nP1")
            // selector turn 1
            .EnqueueText("[[ ## NextSpeaker ## ]]\ncoder")
            // coder's Predict
            .EnqueueText("[[ ## Code ## ]]\nC1")
            // (handoff override -> critic; no selector call)
            // critic's Predict
            .EnqueueText("[[ ## Critique ## ]]\nDONE");

        var planner = new PredictAgent<PlanSig>("planner", "planner", chat,
            v => new AgentMessage("planner", "planner", v.Plan));
        var coder = new PredictAgent<CodeSig>("coder", "coder", chat,
            v => new AgentMessage("coder", "coder", v.Code) { Handoff = "critic" });
        var critic = new PredictAgent<CritSig>("critic", "critic", chat,
            v => new AgentMessage("critic", "critic", v.Critique) { IsTerminal = true });

        var sel = new LlmDrivenSelector(chat, NullLogger<LlmDrivenSelector>.Instance);
        var groupChat = new GroupChat([planner, coder, critic], sel);
        var result = await groupChat.RunAsync("Solve task X", maxTurns: 10, TestContext.Current.CancellationToken);

        result.Reason.ShouldBe(TerminationReason.TerminalMessage);
        // system + planner + coder + critic
        result.History.Count.ShouldBe(4);
        result.History[1].SenderName.ShouldBe("planner");
        result.History[2].SenderName.ShouldBe("coder");
        result.History[3].SenderName.ShouldBe("critic");
        result.History[3].IsTerminal.ShouldBeTrue();
    }
}
