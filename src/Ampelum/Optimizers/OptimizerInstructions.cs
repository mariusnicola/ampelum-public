namespace SignalGrid.Ampelum.Optimizers;

internal static class OptimizerInstructions
{
    /// <summary>
    /// The instruction a submodule sends now: its override when there is one, else the instruction of the
    /// first signature type among the generic arguments of the module.
    /// </summary>
    internal static string Current(object program, string submodule, IReadOnlyDictionary<string, string> overrides)
    {
        if (overrides.TryGetValue(submodule, out var overridden)) return overridden;
        object module = program is IComposedModule composed && composed.Submodules.TryGetValue(submodule, out var sub)
            ? sub
            : program;
        var signature = module.GetType().GetGenericArguments().FirstOrDefault(typeof(ISignature).IsAssignableFrom);
        return signature is null
            ? "(the module's own instruction)"
            : SignatureMetadataCache.For(signature).Instructions;
    }
}
