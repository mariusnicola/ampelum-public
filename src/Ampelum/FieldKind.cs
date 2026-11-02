namespace SignalGrid.Ampelum;

/// <summary>Distinguishes whether a <see cref="Field"/> is an input or an output in a Ampelum signature.</summary>
public enum FieldKind
{
    /// <summary>The field is supplied by the caller and included in the LM prompt.</summary>
    Input,

    /// <summary>The field is produced by the LM and extracted from its response.</summary>
    Output
}
