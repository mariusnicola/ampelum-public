using System.Reflection;

namespace SignalGrid.Ampelum;

/// <summary>
/// A labelled demonstration pairing input values with expected output values for a Ampelum signature.
/// Demos are passed to <see cref="IAdapter.Format"/> to provide few-shot examples in the prompt.
/// </summary>
/// <param name="Inputs">Dictionary of input field names to their values.</param>
/// <param name="Outputs">Dictionary of output field names to their expected values.</param>
public sealed record Example(
    IReadOnlyDictionary<string, object?> Inputs,
    IReadOnlyDictionary<string, object?> Outputs)
{
    /// <summary>
    /// Constructs an <see cref="Example"/> by reflecting over a fully-populated signature instance.
    /// All inputs and outputs are extracted by property name.
    /// </summary>
    /// <typeparam name="TSig">The signature type.</typeparam>
    /// <param name="populated">A fully-populated signature record with both input and output values set.</param>
    /// <returns>An <see cref="Example"/> whose <see cref="Inputs"/> and <see cref="Outputs"/> match the instance's properties.</returns>
    public static Example From<TSig>(TSig populated) where TSig : ISignature
    {
        ArgumentNullException.ThrowIfNull(populated);
        var meta = SignatureMetadataCache.For<TSig>();
        var ins = new Dictionary<string, object?>(meta.Inputs.Count);
        var outs = new Dictionary<string, object?>(meta.Outputs.Count);
        foreach (var f in meta.Inputs)  ins[f.Name]  = Read(populated, f.Name);
        foreach (var f in meta.Outputs) outs[f.Name] = Read(populated, f.Name);
        return new Example(ins, outs);

        static object? Read(object instance, string propName) =>
            instance.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance)!.GetValue(instance);
    }
}
