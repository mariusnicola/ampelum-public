using System.Text.Json;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Ampelum.Templates.Tests;

public class EnterpriseTemplateTests
{
    private static readonly string Root = TemplateRoot.TemplateDir("ampelum-enterprise");

    [Fact]
    public void TemplateJson_ShortNameIsAmpelumEnterprise()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, ".template.config", "template.json")));
        doc.RootElement.GetProperty("shortName").GetString().ShouldBe("ampelum-enterprise");
    }

    [Fact]
    public void Csproj_HasPublishAotAndIsTrimmable()
    {
        var csproj = XDocument.Load(Path.Combine(Root, "Company.Enterprise.csproj"));
        csproj.Descendants("PublishAot").Single().Value.ShouldBe("true");
        csproj.Descendants("IsTrimmable").Single().Value.ShouldBe("true");
    }

    [Fact]
    public void Program_CallsAuditAndBudgetExtensions()
    {
        var prog = File.ReadAllText(Path.Combine(Root, "Program.cs"));
        prog.ShouldContain("AddAmpelumAudit");
        prog.ShouldContain("AddAmpelumBudget");
        prog.ShouldContain("AddAmpelum()");
    }

    [Fact]
    public void AuditAndBudgetConfigurationFiles_Exist()
    {
        File.Exists(Path.Combine(Root, "Audit", "AuditConfiguration.cs")).ShouldBeTrue();
        File.Exists(Path.Combine(Root, "Budget", "BudgetConfiguration.cs")).ShouldBeTrue();
    }

    [Fact]
    public void ThreatModel_DocumentsTrustBoundaries()
    {
        var tm = File.ReadAllText(Path.Combine(Root, "docs", "THREAT-MODEL.md"));
        tm.ShouldContain("Trust boundaries");
        tm.ShouldContain("Audit");
        tm.ShouldContain("Budget");
    }
}
