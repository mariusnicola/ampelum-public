using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Testing;

/// <summary>
/// An immutable snapshot of a single call to <see cref="FakeChatClient.GetResponseAsync"/>.
/// Inspect <see cref="Messages"/> and <see cref="Options"/> to verify what was sent to the LM.
/// </summary>
/// <param name="Messages">The list of chat messages passed to the call.</param>
/// <param name="Options">The <see cref="ChatOptions"/> passed to the call; <see langword="null"/> if none were supplied.</param>
/// <param name="CancellationToken">The cancellation token passed to the call.</param>
public sealed record RecordedChatCall(
    IReadOnlyList<ChatMessage> Messages,
    ChatOptions? Options,
    CancellationToken CancellationToken);
