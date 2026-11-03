using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Optimizers;

/// <summary>Training data for one submodule.</summary>
/// <param name="Submodule">Name of the submodule the examples come from.</param>
/// <param name="Examples">
/// Chat-format examples. Each one is the messages the submodule sent in a kept call (instruction override and
/// demos included), followed by one assistant message with the response of that call.
/// </param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record FineTuneRequest(string Submodule, IReadOnlyList<IReadOnlyList<ChatMessage>> Examples);

/// <summary>
/// A service that fine-tunes a model. <see cref="BootstrapFinetune{TProgram}"/> calls it once per submodule.
/// The library ships no implementation.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IFineTuneProvider
{
    /// <summary>Trains on <paramref name="request"/> and returns a client for the tuned model.</summary>
    /// <param name="request">The submodule and its training examples.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IChatClient> FineTuneAsync(FineTuneRequest request, CancellationToken ct = default);
}
