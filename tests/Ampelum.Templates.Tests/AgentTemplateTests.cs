using System.Text.Json;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Ampelum.Templates.Tests;

public class AgentTemplateTests
{
    private static readonly string Root = TemplateRoot.TemplateDir("ampelum-agent");

    [Fact]
    public void TemplateJson_ShortName()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, ".template.config", "template.json")));
        doc.RootElement.GetProperty("shortName").GetString().ShouldBe("ampelum-agent");
    }

    [Fact]
    public void Researcher_AndCritic_ImplementIAgent()
    {
        File.ReadAllText(Path.Combine(Root, "Agents", "Researcher.cs")).ShouldContain(": IAgent");
        File.ReadAllText(Path.Combine(Root, "Agents", "Critic.cs")).ShouldContain(": IAgent");
    }

    [Fact]
    public void CalculatorTool_ImplementsITypedTool()
    {
        var src = File.ReadAllText(Path.Combine(Root, "Tools", "CalculatorTool.cs"));
        src.ShouldContain("ITypedTool<double>");
        src.ShouldContain("InvokeAsync");
    }

    [Fact]
    public void Program_BuildsGroupChat_AndRunsTurns()
    {
        var prog = File.ReadAllText(Path.Combine(Root, "Program.cs"));
        prog.ShouldContain("new GroupChat(");
        prog.ShouldContain("Researcher");
        prog.ShouldContain("Critic");
        prog.ShouldContain("RoundRobinSelector");
        prog.ShouldContain("RunAsync");
    }

    [Fact]
    public void Csproj_SuppressesPreviewExperimentalWarning()
    {
        var csproj = XDocument.Load(Path.Combine(Root, "Company.Agent.csproj"));
        csproj.Descendants("NoWarn").Single().Value.ShouldContain("AMPELUM_V09_PREVIEW");
    }
}
