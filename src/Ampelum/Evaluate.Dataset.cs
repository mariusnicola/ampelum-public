using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using SignalGrid.Ampelum.Evaluation;

namespace SignalGrid.Ampelum;

public sealed partial class Evaluate<TSig>
{
    /// <summary>
    /// Runs the program over rows materialised from <paramref name="dataset"/>. Each row dictionary
    /// is mapped to <see cref="LabeledExample{TSig}"/> by populating the signature's input fields
    /// from matching keys (inputs only) and the expected fields from matching keys (outputs only).
    /// Missing keys leave the field at its default value; extra keys are ignored.
    /// </summary>
    /// <param name="dataset">Re-iterable dataset whose rows are materialised into examples.</param>
    /// <param name="ct">Cancellation token.</param>
    [Experimental("AMPELUM_V09_PREVIEW")]
    public async Task<EvaluationResult<TSig>> RunAsync(
        IDataset dataset,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        var examples = new List<LabeledExample<TSig>>(checked((int)(dataset.RowCount ?? 64)));
        await foreach (var row in dataset.EnumerateAsync(ct).ConfigureAwait(false))
            examples.Add(MapRow(row));
        return await RunAsync(examples, ct).ConfigureAwait(false);
    }

    static LabeledExample<TSig> MapRow(IReadOnlyDictionary<string, object?> row)
    {
        var meta = SignatureMetadataCache.For<TSig>();
        var inputs = (TSig)Activator.CreateInstance(typeof(TSig))!;
        var expected = (TSig)Activator.CreateInstance(typeof(TSig))!;
        foreach (var f in meta.Inputs)
            if (row.TryGetValue(f.Name, out var v)) Set(inputs, f.Name, v);
        foreach (var f in meta.Outputs)
            if (row.TryGetValue(f.Name, out var v)) Set(expected, f.Name, v);
        return new LabeledExample<TSig>(inputs, expected);
    }

    static void Set(object instance, string propName, object? value)
    {
        var prop = typeof(TSig).GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || !prop.CanWrite) return;
        var target = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        var converted = value is null
            ? null
            : (target.IsInstanceOfType(value)
                ? value
                : Convert.ChangeType(value, target, CultureInfo.InvariantCulture));
        prop.SetValue(instance, converted);
    }
}
