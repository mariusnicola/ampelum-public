using System.Reflection;
using SignalGrid.Ampelum.Exceptions;

namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// Immutable snapshot of an optimized program: the compiled <typeparamref name="TProgram"/>
/// instance together with the per-submodule demo lists, instruction overrides, and optional
/// evaluation metadata produced by <see cref="IOptimizer{TProgram}.CompileAsync"/>.
/// </summary>
/// <typeparam name="TProgram">The user program type.</typeparam>
/// <param name="Program">The compiled program instance, ready to call via <see cref="Bind"/>.</param>
/// <param name="DemosBySubmodule">Per-submodule demo lists keyed by submodule name.</param>
/// <param name="InstructionOverrides">Per-submodule instruction overrides keyed by submodule name.</param>
public sealed record OptimizedProgram<
    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods
        | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicMethods)]
    TProgram>(
    TProgram Program,
    IReadOnlyDictionary<string, IReadOnlyList<Example>> DemosBySubmodule,
    IReadOnlyDictionary<string, string> InstructionOverrides)
{
    /// <summary>Mean score on the trainset as evaluated by the optimizer's metric. Null if not computed.</summary>
    public double? Score { get; init; }

    /// <summary>Number of trainset examples used during compilation.</summary>
    public int TrainsetSize { get; init; }

    /// <summary>
    /// Returns a bound copy of <see cref="Program"/> where each submodule's
    /// <see cref="AmpelumContext"/> carries the optimized demos and instruction overrides.
    ///
    /// Path A (preferred): if <typeparamref name="TProgram"/> implements
    /// <see cref="IComposedModule"/>, iterates <c>Submodules</c>, builds a per-submodule
    /// <see cref="AmpelumContext"/> per Â§7.1.2, calls <c>WithContext</c> via reflection on each
    /// module, then calls <c>Rebind</c> to produce a new program instance.
    ///
    /// Path B (fallback): if <typeparamref name="TProgram"/> exposes a single
    /// <c>WithContext(AmpelumContext)</c> method, calls it directly with a context carrying
    /// the first submodule's demos and overrides. Path B is implemented in Task 5.
    /// </summary>
    public TProgram Bind(AmpelumContext baseContext)
    {
        ArgumentNullException.ThrowIfNull(baseContext);

        if (Program is IComposedModule composed)
            return BindPathA(composed, baseContext);

        return BindPathB(baseContext);
    }

    TProgram BindPathA(IComposedModule composed, AmpelumContext baseContext)
    {
        var newSubmodules = new Dictionary<string, object>(composed.Submodules.Count);

        foreach (var (name, module) in composed.Submodules)
        {
            DemosBySubmodule.TryGetValue(name, out var demos);
            InstructionOverrides.TryGetValue(name, out var instruction);

            var perCtx = baseContext with
            {
                SubmoduleName = name,
                DefaultDemos  = (demos is { Count: > 0 }) ? demos : null,
                InstructionOverrides = (instruction is not null)
                    ? new Dictionary<string, string> { [name] = instruction }
                    : null,
            };

            var reboundModule = CallWithContext(module, perCtx);
            newSubmodules[name] = reboundModule;
        }

        return (TProgram)composed.Rebind(newSubmodules);
    }

    TProgram BindPathB(AmpelumContext baseContext)
    {
        // Use the first DemosBySubmodule entry's value (if any) as the DefaultDemos,
        // and the InstructionOverrides dict (if any) as-is.
        IReadOnlyList<Example>? demos = null;
        if (DemosBySubmodule.Count > 0)
        {
            var first = DemosBySubmodule.First();
            demos = first.Value.Count > 0 ? first.Value : null;
        }

        IReadOnlyDictionary<string, string>? overrides = null;
        if (InstructionOverrides.Count > 0)
            overrides = InstructionOverrides;

        var singleCtx = baseContext with
        {
            DefaultDemos          = demos,
            InstructionOverrides  = overrides,
        };

        return (TProgram)CallWithContext(Program!, singleCtx);
    }

    /// <summary>
    /// Calls <c>module.WithContext(ctx)</c> via reflection. Reflection is required because
    /// C# pattern matching cannot bind open generic type parameters at runtime (PSQ-5).
    /// </summary>
    static object CallWithContext(object module, AmpelumContext ctx)
    {
        var method = module.GetType().GetMethod(
            "WithContext",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: [typeof(AmpelumContext)],
            modifiers: null);
        if (method is null)
            throw new OptimizerException(
                $"Module '{module.GetType().FullName}' does not expose a 'WithContext(AmpelumContext)' method. " +
                "Implement WithContext or have the program implement IComposedModule.");

        return method.Invoke(module, [ctx])
            ?? throw new OptimizerException(
                $"Module '{module.GetType().FullName}'.WithContext returned null.");
    }
}
