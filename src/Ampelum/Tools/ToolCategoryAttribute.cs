namespace SignalGrid.Ampelum.Tools;

/// <summary>
/// Classifies an <see cref="ITypedTool{TResult}"/> implementation by its category of effect. PSQ-7.
/// AMP002 fires on tool types that lack this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ToolCategoryAttribute : Attribute
{
    /// <summary>Classification kind carried by this attribute instance.</summary>
    public ToolCategoryKind Kind { get; }

    /// <summary>Initializes the attribute with the given category.</summary>
    /// <param name="kind">Category of effect for the annotated tool type.</param>
    public ToolCategoryAttribute(ToolCategoryKind kind) => Kind = kind;
}
