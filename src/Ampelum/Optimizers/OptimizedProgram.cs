using System.Reflection;
using Microsoft.Extensions.AI;
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
    /// Per-submodule chat clients (for example fine-tuned ones) keyed by submodule name. <see cref="Bind"/>
    /// gives a submodule its client in place of the base context's one. Not written by program persistence.
    /// </summary>
    public IReadOnlyDictionary<string, IChatClient> ChatClients { get; init; } =
        new Dictionary<string, IChatClient>();

    /// <summary>Score after each step of a multi-step optimizer, in step order. Null for single-step optimizers.</summary>
    public IReadOnlyList<double>? StepScores { get; init; }

    /// <summary>
    /// Returns a bound copy of <see cref="Program"/> where each submodule's
    /// <see cref="AmpelumContext"/> carries the optimized demos and instruction overrides.
    ///
    /// A client, demo list or instruction override this program sets replaces the one the module is bound to.
    /// One it does not set stays when an earlier <c>Bind</c> set it on that module (the module's context carries
    /// a mark for it), so the candidates of a later optimizer keep the work of an earlier step. A module bound
    /// by your own code, with <c>WithContext</c>, carries no mark: it gets the client of <paramref name="baseContext"/>
    /// and no demos or override beyond what this program sets, as before. This carry-over works for <c>Predict</c>
    /// and <c>ChainOfThought</c> submodules only; any other module is bound as before.
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

            // A field this program sets replaces the module's bound value. A field it leaves out stays when an
            // earlier Bind set it (the module's context carries the mark), and is the context's otherwise.
            var current = (module as IBoundContext)?.BoundContext;
            var carried = current?.OptimizerBound ?? BoundFields.None;
            var marks = BoundFields.None;

            var perCtx = baseContext with { SubmoduleName = name };

            if (demos is { Count: > 0 })
            {
                perCtx = perCtx with { DefaultDemos = demos };
                marks |= BoundFields.Demos;
            }
            else if (carried.HasFlag(BoundFields.Demos))
            {
                perCtx = perCtx with { DefaultDemos = current!.DefaultDemos };
                marks |= BoundFields.Demos;
            }
            else
                perCtx = perCtx with { DefaultDemos = null };

            if (instruction is not null)
            {
                perCtx = perCtx with { InstructionOverrides = new Dictionary<string, string> { [name] = instruction } };
                marks |= BoundFields.Instructions;
            }
            else if (carried.HasFlag(BoundFields.Instructions))
            {
                perCtx = perCtx with { InstructionOverrides = current!.InstructionOverrides };
                marks |= BoundFields.Instructions;
            }
            else
                perCtx = perCtx with { InstructionOverrides = null };

            if (ChatClients.TryGetValue(name, out var client))
            {
                perCtx = perCtx with { Chat = client };
                marks |= BoundFields.Client;
            }
            else if (carried.HasFlag(BoundFields.Client))
            {
                perCtx = perCtx with { Chat = current!.Chat };
                marks |= BoundFields.Client;
            }

            perCtx = perCtx with { OptimizerBound = marks };

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

        // A field this program sets replaces the module's bound value. A field it leaves out stays when an
        // earlier Bind set it (the module's context carries the mark), and is the context's otherwise.
        var current = (Program as IBoundContext)?.BoundContext;
        var carried = current?.OptimizerBound ?? BoundFields.None;
        var marks = BoundFields.None;

        var singleCtx = baseContext;

        if (demos is not null)
        {
            singleCtx = singleCtx with { DefaultDemos = demos };
            marks |= BoundFields.Demos;
        }
        else if (carried.HasFlag(BoundFields.Demos))
        {
            singleCtx = singleCtx with { DefaultDemos = current!.DefaultDemos };
            marks |= BoundFields.Demos;
        }
        else
            singleCtx = singleCtx with { DefaultDemos = null };

        if (overrides is not null)
        {
            singleCtx = singleCtx with { InstructionOverrides = overrides };
            marks |= BoundFields.Instructions;
            // A module reads an instruction override only under its submodule name, so the context must
            // carry the name the override is stored under. With several keys the module's own name stays.
            if (overrides.Count == 1)
                singleCtx = singleCtx with { SubmoduleName = overrides.Keys.First() };
        }
        else if (carried.HasFlag(BoundFields.Instructions))
        {
            singleCtx = singleCtx with
            {
                InstructionOverrides = current!.InstructionOverrides,
                SubmoduleName = current.SubmoduleName,
            };
            marks |= BoundFields.Instructions;
        }
        else
            singleCtx = singleCtx with { InstructionOverrides = null };

        // One client is the single module's. Several clients cannot be told apart, so the module keeps its own.
        if (ChatClients.Count == 1)
        {
            singleCtx = singleCtx with { Chat = ChatClients.First().Value };
            marks |= BoundFields.Client;
        }
        else if (carried.HasFlag(BoundFields.Client))
        {
            singleCtx = singleCtx with { Chat = current!.Chat };
            marks |= BoundFields.Client;
        }

        return (TProgram)CallWithContext(Program!, singleCtx with { OptimizerBound = marks });
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
