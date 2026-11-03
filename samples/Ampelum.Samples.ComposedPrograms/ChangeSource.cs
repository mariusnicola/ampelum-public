using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Evaluation;

namespace Ampelum.Samples.ComposedPrograms;

/// <summary>Reads the changes of the release and the approved notes of the previous one from CSV files.</summary>
public sealed class ChangeSource(string dataDirectory)
{
    public const string ChangesFile = "changes.csv";
    public const string ApprovedFile = "approved-notes.csv";

    public string DataDirectory { get; } = dataDirectory;

    public string PathOf(string file) => Path.Combine(DataDirectory, file);

    public CsvDataset Changes() => CsvDataset.FromFile(PathOf(ChangesFile));

    public CsvDataset Approved() => CsvDataset.FromFile(PathOf(ApprovedFile));

    public async Task<IReadOnlyList<ChangeRow>> LoadChangesAsync(CancellationToken ct = default)
    {
        var rows = new List<ChangeRow>();
        await foreach (var row in Changes().EnumerateAsync(ct).ConfigureAwait(false))
            rows.Add(new ChangeRow(Text(row, "Id"), Text(row, "Area"), Text(row, "Title"), Text(row, "Details")));
        return rows;
    }

    /// <summary>Previous release notes as training examples: inputs are the change, outputs the approved note.</summary>
    public async Task<IReadOnlyList<LabeledExample<DraftSig>>> LoadApprovedAsync(CancellationToken ct = default)
    {
        var examples = new List<LabeledExample<DraftSig>>();
        await foreach (var row in Approved().EnumerateAsync(ct).ConfigureAwait(false))
        {
            var inputs = new DraftSig
            {
                Title = Text(row, "Title"),
                Details = Text(row, "Details"),
                Kind = Text(row, "Kind"),
                Impact = int.Parse(Text(row, "Impact"), System.Globalization.CultureInfo.InvariantCulture),
            };
            examples.Add(new LabeledExample<DraftSig>(inputs, inputs with { Note = Text(row, "Note") }));
        }
        return examples;
    }

    static string Text(IReadOnlyDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is string text
            ? text
            : throw new InvalidDataException($"Column {column} is missing.");
}
