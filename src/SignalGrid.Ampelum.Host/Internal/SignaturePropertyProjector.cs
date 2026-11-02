using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using SignalGrid.Ampelum;

namespace SignalGrid.Ampelum.Host.Internal;

[Experimental("AMPELUM_V09_PREVIEW")]
internal static class SignaturePropertyProjector
{
    public static TSig ProjectTo<TSig>(IReadOnlyDictionary<string, object?> dict)
        where TSig : ISignature
    {
        var meta = SignatureMetadataCache.For<TSig>();
        var instance = (TSig)Activator.CreateInstance(typeof(TSig))!;
        var props = typeof(TSig).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var propsByName = props.ToDictionary(p => p.Name, p => p, StringComparer.Ordinal);

        for (var i = 0; i < meta.Inputs.Count; i++)
        {
            var field = meta.Inputs[i];
            if (!dict.TryGetValue(field.Name, out var value))
            {
                if (!field.IsOptional)
                    throw new ArgumentException(
                        $"Missing required input field '{field.Name}'.",
                        nameof(dict));
                value = GetDefaultForType(field.ClrType);
            }
            if (propsByName.TryGetValue(field.Name, out var prop))
                prop.SetValue(instance, value);
        }

        return instance;
    }

    public static IReadOnlyDictionary<string, object?> ProjectFromOutputs<TSig>(Prediction<TSig> prediction)
        where TSig : ISignature
    {
        var meta = SignatureMetadataCache.For<TSig>();
        var dict = new Dictionary<string, object?>(meta.Outputs.Count, StringComparer.Ordinal);
        var outputs = prediction.Value;
        var t = typeof(TSig);
        foreach (var f in meta.Outputs)
            dict[f.Name] = t.GetProperty(f.Name)!.GetValue(outputs);
        return dict;
    }

    static object? GetDefaultForType(Type t)
    {
        if (t.IsValueType)
            return Activator.CreateInstance(t);
        return null;
    }
}
