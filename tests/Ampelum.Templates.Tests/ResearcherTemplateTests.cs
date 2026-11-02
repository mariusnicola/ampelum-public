using System.Text.Json;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Ampelum.Templates.Tests;

public class ResearcherTemplateTests
{
    private static readonly string Root = TemplateRoot.TemplateDir("ampelum-researcher");

    [Fact]
    public void TemplateJson_DeclaresShortName()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, ".template.config", "template.json")));
        doc.RootElement.GetProperty("shortName").GetString().ShouldBe("ampelum-researcher");
        doc.RootElement.GetProperty("sourceName").GetString().ShouldBe("Company.Research");
    }

    [Fact]
    public void Csproj_IsConsoleAppReferencingAmpelumAndSpectre()
    {
        var csproj = XDocument.Load(Path.Combine(Root, "Company.Research.csproj"));
        csproj.Descendants("OutputType").Single().Value.ShouldBe("Exe");
        var refs = csproj.Descendants("PackageReference")
            .Select(e => e.Attribute("Include")!.Value)
            .ToHashSet();
        refs.ShouldContain("Ampelum");
        refs.ShouldContain("SignalGrid.Ampelum.Testing");
        refs.ShouldContain("Spectre.Console");
    }

    [Fact]
    public void Program_UsesFakeChatClientAndRendersResultHeading()
    {
        var prog = File.ReadAllText(Path.Combine(Root, "Program.cs"));
        prog.ShouldContain("FakeChatClient");
        prog.ShouldContain("Predict.Create<SummariseSignature>");
        prog.ShouldContain("## Result");
    }

    [Fact]
    public void TrainingDataset_HasThreeRows()
    {
        var lines = File.ReadAllLines(Path.Combine(Root, "data", "train.jsonl"))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();
        lines.Length.ShouldBe(3);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            doc.RootElement.TryGetProperty("article", out _).ShouldBeTrue();
            doc.RootElement.TryGetProperty("summary", out _).ShouldBeTrue();
        }
    }

    [Fact]
    public void Readme_MentionsBootstrapFewShot()
    {
        File.ReadAllText(Path.Combine(Root, "README.md")).ShouldContain("BootstrapFewShot");
    }
}
