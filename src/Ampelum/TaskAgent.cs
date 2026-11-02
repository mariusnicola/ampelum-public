using System.Threading.Channels;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum;

/// <summary>Runs one task autonomously — wraps StreamingChatSession in a background loop with event streaming.</summary>
public sealed class TaskAgent : ITaskAgent
{
    const int MaxTurns = 30;
    const string CompletionMarker = "TASK_COMPLETE";

    readonly AmpelumContext _ctx;
    readonly string _systemPrompt;
    readonly IList<AITool>? _tools;
    readonly Channel<AgentEvent> _channel = Channel.CreateBounded<AgentEvent>(1000);
    readonly CancellationTokenSource _cts = new();
    Task? _loop;
    string? _interruptMessage;

    /// <inheritdoc/>
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];

    /// <inheritdoc/>
    public AgentStatus Status { get; private set; } = AgentStatus.Idle;

    /// <inheritdoc/>
    public ChannelReader<AgentEvent> Events => _channel.Reader;

    /// <summary>Creates a TaskAgent bound to the given context.</summary>
    public TaskAgent(AmpelumContext ctx, string systemPrompt, IList<AITool>? tools)
    {
        _ctx = ctx;
        _systemPrompt = systemPrompt;
        _tools = tools;
    }

    /// <inheritdoc/>
    public Task StartAsync(string taskInstruction, CancellationToken ct = default)
    {
        Status = AgentStatus.Running;
        _channel.Writer.TryWrite(new StatusEvent(AgentStatus.Running, "Starting"));
        _loop = Task.Run(() => RunLoopAsync(taskInstruction, ct), ct);
        return Task.CompletedTask;
    }

    async Task RunLoopAsync(string instruction, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        try
        {
            var session = new StreamingChatSession(_ctx, _systemPrompt, _tools);
            for (int turn = 1; turn <= MaxTurns; turn++)
            {
                var msg = turn == 1 ? instruction : _interruptMessage ?? "Continue.";
                _interruptMessage = null;

                var response = new System.Text.StringBuilder();
                await foreach (var token in session.SendAsync(msg, linked.Token))
                {
                    response.Append(token);
                    _channel.Writer.TryWrite(new TokenEvent(token));
                }

                var full = response.ToString();
                var doneIdx = full.LastIndexOf(CompletionMarker, StringComparison.Ordinal);
                if (doneIdx >= 0)
                {
                    var summary = full[(doneIdx + CompletionMarker.Length)..].Trim();
                    var usage = session.LastUsage;
                    Status = AgentStatus.Completed;
                    _channel.Writer.TryWrite(new CompleteEvent(
                        summary, usage?.InputTokens ?? 0, usage?.OutputTokens ?? 0, usage?.EstimatedCostUsd));
                    _channel.Writer.TryComplete();
                    return;
                }
            }
            Status = AgentStatus.Completed;
            _channel.Writer.TryWrite(new CompleteEvent("Max turns reached", 0, 0, null));
            _channel.Writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            Status = AgentStatus.Stopped;
            _channel.Writer.TryWrite(new StatusEvent(AgentStatus.Stopped, "Stopped by user"));
            _channel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            Status = AgentStatus.Error;
            _channel.Writer.TryWrite(new ErrorEvent("AGENT_ERROR", ex.Message));
            _channel.Writer.TryComplete();
        }
    }

    /// <inheritdoc/>
    public Task StopAsync()
    {
        _cts.Cancel();
        Status = AgentStatus.Stopped;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task InterruptAsync(string message, CancellationToken ct = default)
    {
        _interruptMessage = message;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
