using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Evaluation;

/// <summary>
/// Re-iterable async row source backing <see cref="Evaluate{TSig}"/> evaluation.
/// </summary>
/// <remarks>
/// Implementations MUST allow <see cref="EnumerateAsync"/> to be called more than once and produce
/// independent traversals (the same dataset may feed an A/B comparison against two programs, or be
/// re-evaluated). File-backed implementations therefore accept a <c>Func&lt;Stream&gt;</c> factory rather
/// than a single <see cref="Stream"/> instance â€” see <see cref="JsonLineDataset"/> and <see cref="CsvDataset"/>.
/// Each row is a string-keyed dictionary; field-name keys must match the input/output
/// <see cref="Field.Name"/> of the target signature.
/// </remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IDataset
{
    /// <summary>Optional row count (for progress reporting). May be <see langword="null"/> for streaming sources before the first full enumeration.</summary>
    long? RowCount { get; }

    /// <summary>Enumerates rows. Each call MUST produce an independent traversal from the start.</summary>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<IReadOnlyDictionary<string, object?>> EnumerateAsync(CancellationToken ct = default);
}
