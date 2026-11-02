using System.Threading.Channels;

namespace SignalGrid.Ampelum;

/// <summary>Represents the current status of an <see cref="ITaskAgent"/>.</summary>
public enum AgentStatus
{
    /// <summary>The agent has been created but not yet started.</summary>
    Idle,
    /// <summary>The agent is actively processing a task.</summary>
    Running,
    /// <summary>The agent has completed its task successfully.</summary>
    Completed,
    /// <summary>The agent encountered an error during execution.</summary>
    Error,
    /// <summary>The agent was stopped by user request.</summary>
    Stopped,
}

/// <summary>
/// Interface for a disposable task-oriented agent that wraps an LLM chat session
/// and emits structured events over a <see cref="ChannelReader{T}"/>.
/// </summary>
public interface ITaskAgent : IDisposable
{
    /// <summary>A unique identifier for this agent instance.</summary>
    string Id { get; }

    /// <summary>The current lifecycle status of the agent.</summary>
    AgentStatus Status { get; }

    /// <summary>A channel through which the agent emits <see cref="AgentEvent"/>s.</summary>
    ChannelReader<AgentEvent> Events { get; }

    /// <summary>Starts the agent with the given task instruction.</summary>
    /// <param name="taskInstruction">The instruction describing the task to perform.</param>
    /// <param name="ct">Cancellation token.</param>
    Task StartAsync(string taskInstruction, CancellationToken ct = default);

    /// <summary>Requests the agent to stop gracefully.</summary>
    Task StopAsync();

    /// <summary>Sends an interrupt message to the running agent.</summary>
    /// <param name="message">The interrupt message.</param>
    /// <param name="ct">Cancellation token.</param>
    Task InterruptAsync(string message, CancellationToken ct = default);
}
