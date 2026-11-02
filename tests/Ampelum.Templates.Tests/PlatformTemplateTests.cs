using System.Text.Json;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Ampelum.Templates.Tests;

public class PlatformTemplateTests
{
    private static readonly string Root = TemplateRoot.TemplateDir("ampelum-platform");

    [Fact]
    public void TemplateJson_ShortName()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, ".template.config", "template.json")));
        doc.RootElement.GetProperty("shortName").GetString().ShouldBe("ampelum-platform");
    }

    [Fact]
    public void TenantScope_DeclaresAsyncLocalAndMiddleware()
    {
        var src = File.ReadAllText(Path.Combine(Root, "Tenancy", "TenantScope.cs"));
        src.ShouldContain("AsyncLocal");
        src.ShouldContain("TenantContext");
        src.ShouldContain("TenantScopeMiddleware");
        src.ShouldContain("X-Tenant-Id");
    }

    [Fact]
    public void ProviderRouting_RegistersTwoKeyedClients()
    {
        var src = File.ReadAllText(Path.Combine(Root, "Routing", "ProviderRouting.cs"));
        src.ShouldContain("AddKeyedSingleton<IChatClient>(\"primary\"");
        src.ShouldContain("AddKeyedSingleton<IChatClient>(\"secondary\"");
        src.ShouldContain("AddProviderRouting");
    }

    [Fact]
    public void Program_WiresMiddlewareAndAddProviderRouting()
    {
        var prog = File.ReadAllText(Path.Combine(Root, "Program.cs"));
        prog.ShouldContain("UseMiddleware<TenantScopeMiddleware>");
        prog.ShouldContain("AddProviderRouting");
        prog.ShouldContain("AddAmpelum");
    }

    [Fact]
    public void Csproj_IsWebProject_AndReferencesAmpelum()
    {
        var csproj = XDocument.Load(Path.Combine(Root, "Company.Platform.csproj"));
        csproj.Root!.Attribute("Sdk")!.Value.ShouldContain("Web");
        csproj.Descendants("PackageReference")
            .Any(e => e.Attribute("Include")?.Value == "Ampelum")
            .ShouldBeTrue();
    }
}
