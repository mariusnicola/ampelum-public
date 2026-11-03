using SignalGrid.Ampelum.Agent;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>
/// Keeps the phase and state of one workflow run, and refuses moves that make no sense, such as starting
/// a phase while the run waits for a person. It lives in memory and is rebuilt after a restart.
/// </summary>
public sealed class RunSupervisor(string runId, string firstPhase)
{
    readonly WorkflowStateMachine _machine = new(runId, firstPhase);

    public WorkflowState State => _machine.CurrentState;

    public string Phase => _machine.CurrentPhase;

    public EscalationContext? Waiting => _machine.CurrentEscalation;

    public (int Total, int Completed, int Failed) Progress => _machine.GetProgress();

    /// <summary>Moves to the next phase. Throws while the run is suspended.</summary>
    public void Enter(string phase) => _machine.AdvancePhase(phase);

    /// <summary>Suspends the run until a person decides.</summary>
    public void WaitForPerson(string tool, string reason) =>
        _machine.Escalate(new EscalationContext(EscalationType.RequiresConfirmation, tool, reason, DateTimeOffset.UtcNow));

    /// <summary>Resumes after the person approved.</summary>
    public void PersonDecided() => _machine.ResolveEscalation();

    public void Count(int total, int completed, int failed) => _machine.UpdateProgress(total, completed, failed);

    public void Finish(bool succeeded)
    {
        if (succeeded)
            _machine.Complete();
        else
            _machine.Fail();
    }
}
