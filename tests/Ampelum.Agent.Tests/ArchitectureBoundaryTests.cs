using System.Reflection;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

/// <summary>Verify the package boundary constraints defined in Spec 1.
/// These tests use reflection to inspect assembly references - no runtime type resolution needed.</summary>
public sealed class ArchitectureBoundaryTests
{
    private static readonly Assembly AgentAssembly = typeof(Ampelum.Agent.AgentIdentity).Assembly;
    private static readonly Assembly ToolsAssembly = typeof(Ampelum.Agent.Tools.EditTool).Assembly;
    private static readonly Assembly SynthesisAssembly = typeof(Ampelum.Agent.Synthesis.ISynthesisInfrastructure).Assembly;
    private static readonly Assembly AmpelumAssembly = typeof(SignalGrid.Ampelum.Predict).Assembly;

    private static bool ReferencesAssembly(Assembly assembly, string targetName)
    {
        return assembly.GetReferencedAssemblies().Any(r =>
            r.Name != null && r.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AmpelumAgent_References_AmpelumCore()
    {
        // Ampelum.Agent intentionally depends on SignalGrid.Ampelum for shared runtime and context primitives.
        // The boundary we enforce is that it still must not depend on Agent.Tools or Agent.Synthesis.
        ReferencesAssembly(AgentAssembly, "SignalGrid.Ampelum").ShouldBeTrue();
    }

    [Fact]
    public void AmpelumAgent_DoesNotReference_AnyAgentPackage()
    {
        // Ampelum.Agent should not reference Tools or Synthesis
        ReferencesAssembly(AgentAssembly, "SignalGrid.Ampelum.Agent.Tools").ShouldBeFalse();
        ReferencesAssembly(AgentAssembly, "SignalGrid.Ampelum.Agent.Synthesis").ShouldBeFalse();
    }

    [Fact]
    public void AmpelumCore_DoesNotReference_AnyAgentPackage()
    {
        // Ampelum (LLM core) must not reference Ampelum.Agent.* packages
        ReferencesAssembly(AmpelumAssembly, "SignalGrid.Ampelum.Agent").ShouldBeFalse();
        ReferencesAssembly(AmpelumAssembly, "SignalGrid.Ampelum.Agent.Tools").ShouldBeFalse();
        ReferencesAssembly(AmpelumAssembly, "SignalGrid.Ampelum.Agent.Synthesis").ShouldBeFalse();
    }

    [Fact]
    public void AmpelumTools_References_AmpelumAgent_Only()
    {
        // Tools -> Ampelum.Agent only. No Ampelum core, no Synthesis.
        ReferencesAssembly(ToolsAssembly, "SignalGrid.Ampelum.Agent").ShouldBeTrue();
        ReferencesAssembly(ToolsAssembly, "SignalGrid.Ampelum").ShouldBeFalse();
        ReferencesAssembly(ToolsAssembly, "SignalGrid.Ampelum.Agent.Synthesis").ShouldBeFalse();
    }

    [Fact]
    public void AmpelumSynthesis_References_AmpelumCore_And_Agent()
    {
        // Synthesis -> Ampelum + Ampelum.Agent
        ReferencesAssembly(SynthesisAssembly, "SignalGrid.Ampelum").ShouldBeTrue();
        ReferencesAssembly(SynthesisAssembly, "SignalGrid.Ampelum.Agent").ShouldBeTrue();
    }

    [Fact]
    public void AmpelumSynthesis_DoesNotReference_Tools()
    {
        // Synthesis should not reference Tools
        ReferencesAssembly(SynthesisAssembly, "SignalGrid.Ampelum.Agent.Tools").ShouldBeFalse();
    }

    [Fact]
    public void NoPackageReferences_AgentRunner()
    {
        // No package references the AgentRunner assembly
        ReferencesAssembly(AgentAssembly, "AgentRunner").ShouldBeFalse();
        ReferencesAssembly(ToolsAssembly, "AgentRunner").ShouldBeFalse();
        ReferencesAssembly(SynthesisAssembly, "AgentRunner").ShouldBeFalse();
    }

    [Fact]
    public void NoCircularDependencies()
    {
        // The dependency graph must remain acyclic even with Agent -> Ampelum:
        // Ampelum -> (nothing); Agent -> Ampelum; Tools -> Agent; Synthesis -> Ampelum + Agent.
        var agentRefs = AgentAssembly.GetReferencedAssemblies().Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        agentRefs.ShouldNotContain("SignalGrid.Ampelum.Agent"); // no self-reference
        agentRefs.ShouldNotContain("SignalGrid.Ampelum.Agent.Tools");
        agentRefs.ShouldNotContain("SignalGrid.Ampelum.Agent.Synthesis");
    }
}
