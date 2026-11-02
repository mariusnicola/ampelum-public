using System.Globalization;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Evaluation;

namespace Ampelum.Samples.TracedPipeline;

/// <summary>Reads the labeled feedback dataset.</summary>
public static class SentimentData
{
    /// <summary>Dataset file copied next to the executable.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "data", "feedback.jsonl");

    /// <summary>Loads every row of the JSONL file as a labeled example.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">A row lacks the feedback text or the label.</exception>
    public static async Task<IReadOnlyList<LabeledExample<SentimentSig>>> LoadAsync(
        string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Dataset file not found: '{path}'.", path);

        var examples = new List<LabeledExample<SentimentSig>>();
        var dataset = JsonLineDataset.FromFile(path);
        await foreach (var row in dataset.EnumerateAsync(ct).ConfigureAwait(false))
        {
            var number = examples.Count + 1;
            var feedback = Text(row, nameof(SentimentSig.Feedback), number, path);
            var label = Text(row, nameof(SentimentSig.Sentiment), number, path);
            examples.Add(new LabeledExample<SentimentSig>(
                new SentimentSig { Feedback = feedback },
                new SentimentSig { Feedback = feedback, Sentiment = label }));
        }

        if (examples.Count == 0)
            throw new InvalidDataException($"Dataset file '{path}' has no rows.");
        return examples;
    }

    static string Text(IReadOnlyDictionary<string, object?> row, string key, int number, string path)
    {
        if (row.TryGetValue(key, out var value)
            && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text)
            return text;
        throw new InvalidDataException($"Row {number} of '{path}' has no '{key}' value.");
    }
}
