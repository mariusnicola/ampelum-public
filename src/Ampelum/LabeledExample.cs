namespace SignalGrid.Ampelum;

/// <summary>
/// A labelled example used by evaluation: the input signature instance paired
/// with the expected output signature instance. Both are of the same <typeparamref name="TSig"/> â€”
/// <see cref="Inputs"/> carries input field values (output fields may be defaults), and
/// <see cref="Expected"/> carries expected output field values (input fields typically mirror <see cref="Inputs"/>).
/// </summary>
public sealed record LabeledExample<TSig>(TSig Inputs, TSig Expected)
    where TSig : ISignature;
