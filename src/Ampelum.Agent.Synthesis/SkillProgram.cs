using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum.Agent;

namespace SignalGrid.Ampelum.Agent.Synthesis;

/// <summary>
/// A multi-module program that wraps a Predict&lt;SkillInstructionSig&gt; for synthesizing
/// skill system prompts. Implements IComposedModule for optimizer rebinding.
/// </summary>
public sealed class SkillProgram : IComposedModule
{
    private const string InstructionSubmoduleName = "instruction";

    public SkillProgram(SkillManifest manifest, Predict<SkillInstructionSig> instructionSubmodule)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(instructionSubmodule);
        Manifest = manifest;
        InstructionSubmodule = instructionSubmodule;
    }

    public SkillManifest Manifest { get; }
    public Predict<SkillInstructionSig> InstructionSubmodule { get; }

    public IReadOnlyDictionary<string, object> Submodules =>
        new Dictionary<string, object> { [InstructionSubmoduleName] = InstructionSubmodule };

    public object Rebind(IReadOnlyDictionary<string, object> newSubmodules)
    {
        if (newSubmodules.TryGetValue(InstructionSubmoduleName, out var sub) && sub is Predict<SkillInstructionSig> newInstruction)
            return new SkillProgram(Manifest, newInstruction);
        return this;
    }
}
