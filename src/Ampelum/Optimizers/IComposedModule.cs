namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// Implemented by multi-module programs that want to participate in Path A of
/// <c>OptimizedProgram&lt;TProgram&gt;.Bind</c>. Provides a stable insertion-ordered
/// map of submodule names to module instances, and a non-mutating <see cref="Rebind"/>
/// factory that returns a new program with the supplied submodule instances.
/// </summary>
public interface IComposedModule
{
    /// <summary>
    /// Stable, insertion-ordered map from submodule name to module instance.
    /// Names must be consistent across calls; order determines iteration order in Bind.
    /// </summary>
    IReadOnlyDictionary<string, object> Submodules { get; }

    /// <summary>
    /// Returns a new program instance with the supplied submodule instances substituted.
    /// Must NOT mutate <c>this</c>.
    /// </summary>
    /// <param name="newSubmodules">Map from submodule name to already-rebound module.</param>
    /// <returns>A new program instance of the same concrete type.</returns>
    object Rebind(IReadOnlyDictionary<string, object> newSubmodules);
}
