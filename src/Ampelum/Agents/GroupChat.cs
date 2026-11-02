using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Agents.Memory;
using SignalGrid.Ampelum.Agents.SpeakerSelectors;
using SignalGrid.Ampelum.Audit;

namespace SignalGrid.Ampelum.Agents;

/// <summary>Reason a <see cref="GroupChat"/> conversation terminated.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public enum TerminationReason
{
    /// <summary>An agent emitted a message with <see cref="AgentMessage.IsTerminal"/> set.</summary>
    TerminalMessage,
    /// <summary>The maximum number of turns was reached.</summary>
    MaxTurnsReached,
    /// <summary>An agent handed off to a name not registered with the chat.</summary>
    HandoffToUnknown,
}

/// <summary>Result of a <see cref="GroupChat.RunAsync(string, int, CancellationToken)"/> invocation.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record GroupChatResult(IReadOnlyList<AgentMessage> History, TerminationReason Reason);

/// <summary>
/// Orchestrates a multi-agent conversation. Each turn: pick a speaker (via <see cref="ISpeakerSelector"/>
/// unless overridden by the previous message's <see cref="AgentMessage.Handoff"/>), invoke the agent,
/// append its message to history, and continue until termination.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class GroupChat
{
    readonly IReadOnlyList<IAgent> _agents;
    readonly ISpeakerSelector _selector;
    readonly IConversationMemory? _memory;
    readonly IAuditSink? _auditSink;

    /// <summary>Initialises a new chat over the given agents and selector with optional shared memory.</summary>
    public GroupChat(IReadOnlyList<IAgent> agents, ISpeakerSelector selector, IConversationMemory? memory = null)
        : this(agents, selector, memory, auditSink: null)
    {
    }

    /// <summary>
    /// Initialises a new chat with an optional <see cref="IAuditSink"/>. When supplied, every
    /// speaker handoff (turn N â†’ turn N+1) emits one <see cref="AuditEntry"/> with
    /// <see cref="AuditEntry.EventType"/> = <c>"agent.handoff"</c>. Inputs carry the source
    /// + target agent names and the turn index; Outputs carry a deterministic hash of the
    /// post-handoff conversation state (length + last-message digest).
    /// v1.0-rc1.alpha.5 (Gap 3).
    /// </summary>
    public GroupChat(
        IReadOnlyList<IAgent> agents,
        ISpeakerSelector selector,
        IConversationMemory? memory,
        IAuditSink? auditSink)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(selector);
        if (agents.Count == 0)
            throw new ArgumentException("At least one agent required.", nameof(agents));
        var distinctNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in agents)
        {
            if (!distinctNames.Add(a.Name))
                throw new ArgumentException($"Duplicate agent name '{a.Name}'.", nameof(agents));
        }
        _agents = [.. agents];
        _selector = selector;
        _memory = memory;
        _auditSink = auditSink;
    }

    /// <summary>Runs the conversation up to <paramref name="maxTurns"/> turns or termination.</summary>
    public async Task<GroupChatResult> RunAsync(string initialPrompt, int maxTurns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialPrompt);
        if (maxTurns <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTurns), maxTurns, "maxTurns must be positive.");

        var history = new List<AgentMessage>
        {
            new("system", "system", initialPrompt),
        };
        if (_memory is not null)
            await _memory.AppendAsync(history[0], cancellationToken).ConfigureAwait(false);

        IAgent? pendingSpeaker = null;
        string? previousSpeaker = null;
        for (var turn = 0; turn < maxTurns; turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ctx = new ConversationContext(history, _memory, turn);

            IAgent speaker;
            string handoffKind;
            if (pendingSpeaker is not null)
            {
                speaker = pendingSpeaker;
                pendingSpeaker = null;
                handoffKind = "explicit";
            }
            else
            {
                speaker = await _selector.SelectAsync(ctx, _agents, cancellationToken).ConfigureAwait(false);
                handoffKind = "selector";
            }

            // alpha.5 (Gap 3): emit one audit row per handoff (source -> selected speaker).
            // history snapshot taken BEFORE the speaker replies so the state hash reflects
            // the basis on which the handoff decision was made.
            if (_auditSink is not null)
            {
                await EmitHandoffAuditAsync(
                    sourceName: previousSpeaker ?? "system",
                    targetName: speaker.Name,
                    turn: turn,
                    handoffKind: handoffKind,
                    history: history,
                    cancellationToken).ConfigureAwait(false);
            }

            var msg = await speaker.ReplyAsync(ctx, cancellationToken).ConfigureAwait(false);
            history.Add(msg);
            previousSpeaker = speaker.Name;
            if (_memory is not null)
                await _memory.AppendAsync(msg, cancellationToken).ConfigureAwait(false);

            if (msg.IsTerminal)
                return new GroupChatResult(history, TerminationReason.TerminalMessage);

            if (!string.IsNullOrEmpty(msg.Handoff))
            {
                var target = _agents.FirstOrDefault(a => string.Equals(a.Name, msg.Handoff, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                    return new GroupChatResult(history, TerminationReason.HandoffToUnknown);
                pendingSpeaker = target;
            }
        }
        return new GroupChatResult(history, TerminationReason.MaxTurnsReached);
    }

    async ValueTask EmitHandoffAuditAsync(
        string sourceName,
        string targetName,
        int turn,
        string handoffKind,
        IReadOnlyList<AgentMessage> history,
        CancellationToken ct)
    {
        var sink = _auditSink!;
        var seq = sink.NextSequence;
        // Walk the chain to derive prev hash (linear scan acceptable: handoffs are
        // typically O(turns) and sinks are bounded). Mirrors AuditHook.GetLastEntryHash.
        byte[] prev = new byte[32];
        if (seq > 0)
        {
            await foreach (var e in sink.EnumerateAsync(ct).ConfigureAwait(false))
                prev = e.EntryHash;
        }
        // State hash: SHA-256 of (history.Count || last-message SenderName || last-message Content).
        // Cheap, deterministic, and sufficient for downstream correlation.
        byte[] stateHash;
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            Span<byte> i32 = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(i32, history.Count); sha.AppendData(i32);
            if (history.Count > 0)
            {
                var last = history[history.Count - 1];
                sha.AppendData(Encoding.UTF8.GetBytes(last.SenderName));
                sha.AppendData(Encoding.UTF8.GetBytes(last.Content));
            }
            stateHash = sha.GetHashAndReset();
        }
        var inputs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["agent.handoff.source"] = sourceName,
            ["agent.handoff.target"] = targetName,
            ["agent.handoff.turn"] = (long)turn,
            ["agent.handoff.kind"] = handoffKind,
        };
        var outputs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["agent.conversation.length"] = (long)history.Count,
            ["agent.conversation.state_hash"] = Convert.ToHexString(stateHash),
        };
        var ts = DateTimeOffset.UtcNow;
        var moduleName = nameof(GroupChat);
        var modelId = "n/a";
        var entryHash = AuditChainHasher.Compute(
            seq, ts, moduleName, modelId, inputs, outputs,
            promptTokens: 0, completionTokens: 0, prevHash: prev);
        var entry = new AuditEntry
        {
            EventType = AuditMetadataKeys.EventTypeAgentHandoff,
            Sequence = seq,
            Timestamp = ts,
            ModuleName = moduleName,
            ModelId = modelId,
            Inputs = inputs,
            Outputs = outputs,
            PromptTokens = 0,
            CompletionTokens = 0,
            PrevHash = prev,
            EntryHash = entryHash,
        };
        await sink.AppendAsync(entry, ct).ConfigureAwait(false);
    }
}
