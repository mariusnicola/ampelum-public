namespace SignalGrid.Ampelum.Optimizers;

/// <summary>The fields of a module's context that <see cref="OptimizedProgram{TProgram}.Bind"/> set.</summary>
[Flags]
internal enum BoundFields
{
    None = 0,
    Client = 1,
    Demos = 2,
    Instructions = 4,
}

/// <summary>
/// A module that can tell the context it is bound to, so <see cref="OptimizedProgram{TProgram}.Bind"/> can keep the
/// client, demos and instruction overrides of an earlier step.
/// </summary>
internal interface IBoundContext
{
    /// <summary>The context the module was bound to, or null for a module that resolves its context per call.</summary>
    AmpelumContext? BoundContext { get; }
}
