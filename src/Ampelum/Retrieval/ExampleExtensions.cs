namespace SignalGrid.Ampelum.Retrieval;

/// <summary>
/// Extension methods bridging <see cref="RetrievedDocument"/> and <see cref="Example"/>
/// for dynamic few-shot demo stores (spec Â§6.1.3).
/// </summary>
public static class ExampleExtensions
{
    /// <summary>
    /// Converts a <see cref="RetrievedDocument"/> to an <see cref="Example"/> by reading
    /// <typeparamref name="TSig"/>'s <c>[Input]</c> field names from the document's
    /// <see cref="RetrievedDocument.Metadata"/> (matched by field name, case-sensitive).
    /// <see cref="Example.Outputs"/> comes from the typed <paramref name="outputs"/> parameter
    /// via <see cref="Example.From{TSig}"/>'s output-field reflection.
    /// Fields missing from <see cref="RetrievedDocument.Metadata"/> are silently omitted from
    /// <see cref="Example.Inputs"/>; callers should ensure the store was populated consistently.
    /// </summary>
    /// <typeparam name="TSig">The signature type whose input field names are used as metadata keys.</typeparam>
    /// <param name="doc">The retrieved document whose metadata carries input field values.</param>
    /// <param name="outputs">A fully-populated signature instance whose output fields become <see cref="Example.Outputs"/>.</param>
    public static Example ToExample<TSig>(
        this RetrievedDocument doc,
        TSig outputs)
        where TSig : ISignature
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(outputs);

        var meta = SignatureMetadataCache.For<TSig>();
        var inputs = new Dictionary<string, object?>(meta.Inputs.Count);
        foreach (var field in meta.Inputs)
        {
            if (doc.Metadata.TryGetValue(field.Name, out var value))
                inputs[field.Name] = value;
            // Missing fields silently omitted per spec Â§6.1.3.
        }

        var fullExample = Example.From(outputs);
        return new Example(inputs, fullExample.Outputs);
    }
}
