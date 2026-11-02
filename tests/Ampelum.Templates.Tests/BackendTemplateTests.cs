using System.Text.Json;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Ampelum.Templates.Tests;

public class BackendTemplateTests
{
    private static readonly string Root = TemplateRoot.TemplateDir("ampelum-backend");

    [Fact]
    public void TemplateJson_DeclaresShortNameAndIdentity()
    {
        var path = Path.Combine(Root, ".template.config", "template.json");
        File.Exists(path).ShouldBeTrue();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        doc.RootElement.GetProperty("shortName").GetString().ShouldBe("ampelum-backend");
        doc.RootElement.GetProperty("identity").GetString().ShouldBe("Ampelum.Templates.Backend");
        doc.RootElement.GetProperty("sourceName").GetString().ShouldBe("Company.WebApi");
    }

    [Fact]
    public void Csproj_IsValidXml_AndReferencesAmpelum()
    {
        var csproj = XDocument.Load(Path.Combine(Root, "Company.WebApi.csproj"));
        csproj.Root!.Attribute("Sdk")!.Value.ShouldContain("Web");
        csproj.Descendants("PackageReference")
            .Any(e => e.Attribute("Include")?.Value == "Ampelum")
            .ShouldBeTrue();
    }

    [Fact]
    public void Program_WiresAddAmpelumAndMapPost()
    {
        var prog = File.ReadAllText(Path.Combine(Root, "Program.cs"));
        prog.ShouldContain("AddAmpelum()");
        prog.ShouldContain("MapPost(\"/ask\"");
        prog.ShouldContain("Predict<QASignature>");
    }

    [Fact]
    public void QASignature_HasInputAndOutput()
    {
        var sig = File.ReadAllText(Path.Combine(Root, "Signatures", "QASignature.cs"));
        sig.ShouldContain("[Input]");
        sig.ShouldContain("[Output]");
        sig.ShouldContain("ISignature");
    }

    [Fact]
    public void Readme_DocumentsQuickstart()
    {
        File.ReadAllText(Path.Combine(Root, "README.md")).ShouldContain("dotnet new ampelum-backend");
    }
}
