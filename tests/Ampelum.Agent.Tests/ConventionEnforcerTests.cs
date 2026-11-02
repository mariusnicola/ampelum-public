using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class ConventionEnforcerTests
{
    // ── Implicit default ──────────────────────────────────────────

    [Fact]
    public void DefaultEnforcer_UsesAmpelumPreset()
    {
        var enforcer = new DefaultConventionEnforcer(new ConventionManifest());
        enforcer.CurrentManifest.Preset.ShouldBeNull(); // Resolve sets preset from loaded
    }

    [Fact]
    public void AmpelumPreset_HasMax400Lines()
    {
        var manifest = KnownPresets.Ampelum;
        manifest.Overrides.ShouldNotBeNull();
        manifest.Overrides!.MaxFileLines.ShouldBe(400);
        manifest.Overrides.MaxPublicMethodsPerClass.ShouldBe(10);
    }

    [Fact]
    public void AmpelumPreset_HasExtraRules()
    {
        var manifest = KnownPresets.Ampelum;
        manifest.ExtraRules.ShouldNotBeNull();
        manifest.ExtraRules!.Count.ShouldBe(2);
        manifest.ExtraRules.ShouldContain(r => r.Rule == "no-dynamic");
        manifest.ExtraRules.ShouldContain(r => r.Rule == "no-bare-llm-prompts");
    }

    // ── Resolve merge ─────────────────────────────────────────────

    [Fact]
    public void Resolve_LoadedManifestWithOverrides_MergesCorrectly()
    {
        var loaded = new ConventionManifest
        {
            Version = 1,
            Preset = "minimal",
            Overrides = new ConventionOverrides { MaxFileLines = 300 },
        };

        var resolved = KnownPresets.Resolve(loaded);
        resolved.Overrides.ShouldNotBeNull();
        resolved.Overrides!.MaxFileLines.ShouldBe(300);
        // Minimal preset has no MaxPublicMethods — should stay null
        resolved.Overrides.MaxPublicMethodsPerClass.ShouldBeNull();
    }

    [Fact]
    public void Resolve_LoadedManifestWithExtraRules_AppendsToPreset()
    {
        var loaded = new ConventionManifest
        {
            Version = 1,
            Preset = "ampelum",
            ExtraRules = new List<ExtraRule>
            {
                new() { Rule = "required-file", Severity = "error", Path = "README.md" },
            },
        };

        var resolved = KnownPresets.Resolve(loaded);
        resolved.ExtraRules.ShouldNotBeNull();
        resolved.ExtraRules.Count.ShouldBe(3); // 2 from ampelum + 1 extra
        resolved.ExtraRules.ShouldContain(r => r.Rule == "required-file");
    }

    [Fact]
    public void Resolve_NullManifest_ReturnsAmpelumPreset()
    {
        var resolved = KnownPresets.Resolve(null);
        resolved.Preset.ShouldBeNull(); // Returns the default preset directly
        resolved.Overrides!.MaxFileLines.ShouldBe(400);
    }

    // ── MaxFileLinesChecker ───────────────────────────────────────

    [Fact]
    public void MaxFileLines_SmallFile_NoViolation()
    {
        var checker = new MaxFileLinesChecker(400);
        var content = string.Join("\n", Enumerable.Range(0, 50).Select(i => $"line {i}"));
        var result = checker.Check("test.cs", content, null);
        result.ShouldBeNull();
    }

    [Fact]
    public void MaxFileLines_LargeFile_ReturnsViolation()
    {
        var checker = new MaxFileLinesChecker(10);
        var content = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"line {i}"));
        var result = checker.Check("test.cs", content, null);
        result.ShouldNotBeNull();
        result!.Rule.ShouldBe("max-file-lines");
        result.Severity.ShouldBe("warning");
        result.Message.ShouldContain("20 lines");
        result.Message.ShouldContain("max: 10");
    }

    // ── NoDynamicChecker ──────────────────────────────────────────

    [Fact]
    public void NoDynamic_CleanCode_NoViolation()
    {
        var checker = new NoDynamicChecker();
        var result = checker.Check("test.cs", "public string Name { get; set; }", null);
        result.ShouldBeNull();
    }

    [Fact]
    public void NoDynamic_ContainsDynamic_ReturnsViolation()
    {
        var checker = new NoDynamicChecker();
        var content = "dynamic x = GetValue();";
        var result = checker.Check("test.cs", content, null);
        result.ShouldNotBeNull();
        result!.Rule.ShouldBe("no-dynamic");
        result.Message.ShouldContain("dynamic");
        result.Line.ShouldBe(1);
    }

    [Fact]
    public void NoDynamic_ContainsDynamic_UsesRuleConfigSeverity()
    {
        var checker = new NoDynamicChecker();
        var ruleConfig = new ExtraRule { Rule = "no-dynamic", Severity = "error" };
        var result = checker.Check("test.cs", "dynamic x = 1;", ruleConfig);
        result.ShouldNotBeNull();
        result!.Severity.ShouldBe("error");
    }

    // ── NoBareLlmPromptsChecker ───────────────────────────────────

    [Fact]
    public void NoBareLlmPrompts_CleanCode_NoViolation()
    {
        var checker = new NoBareLlmPromptsChecker();
        var content = "var result = await _client.GetAsync(url);";
        var result = checker.Check("test.cs", content, null);
        result.ShouldBeNull();
    }

    [Fact]
    public void NoBareLlmPrompts_NewPredict_ReturnsViolation()
    {
        var checker = new NoBareLlmPromptsChecker();
        var content = "var prediction = new Predict<Weather>(model);";
        var result = checker.Check("test.cs", content, null);
        result.ShouldNotBeNull();
        result!.Rule.ShouldBe("no-bare-llm-prompts");
        result.Severity.ShouldBe("error");
        result.Message.ShouldContain("bare LLM calls");
    }

    [Fact]
    public void NoBareLlmPrompts_GetResponseAsync_ReturnsViolation()
    {
        var checker = new NoBareLlmPromptsChecker();
        var content = "var reply = await chat.GetResponseAsync(messages);";
        var result = checker.Check("test.cs", content, null);
        result.ShouldNotBeNull();
        result!.Rule.ShouldBe("no-bare-llm-prompts");
    }

    // ── MaxPublicMethodsChecker ───────────────────────────────────

    [Fact]
    public void MaxPublicMethods_BelowThreshold_NoViolation()
    {
        var checker = new MaxPublicMethodsChecker(5);
        var content = @"
public void MethodA(string x) { }
public int MethodB() => 1;
";
        var result = checker.Check("test.cs", content, null);
        result.ShouldBeNull();
    }

    [Fact]
    public void MaxPublicMethods_AboveThreshold_ReturnsViolation()
    {
        var checker = new MaxPublicMethodsChecker(2);
        var content = @"
public void A() { }
public void B() { }
public void C() { }
";
        var result = checker.Check("test.cs", content, null);
        result.ShouldNotBeNull();
        result!.Message.ShouldContain("3 public method");
    }

    // ── RequiredFileChecker ───────────────────────────────────────

    [Fact]
    public void RequiredFile_NoRuleConfig_ReturnsNull()
    {
        var checker = new RequiredFileChecker();
        var result = checker.Check("test.cs", "", null);
        result.ShouldBeNull();
    }

    // ── Integration: DefaultConventionEnforcer.CheckAsync ─────────

    [Fact]
    public async Task CheckAsync_CleanFile_ReturnsEmpty()
    {
        var manifest = new ConventionManifest
        {
            Version = 1,
            Preset = "minimal",
        };
        var enforcer = new DefaultConventionEnforcer(manifest);
        var content = "// clean C# file\nclass Foo { }";
        var violations = await enforcer.CheckAsync("test.cs", content);
        violations.ShouldBeEmpty();
    }

    [Fact]
    public async Task CheckAsync_DynamicKeyword_ReturnsViolation()
    {
        var manifest = new ConventionManifest
        {
            Version = 1,
            Preset = "ampelum",
        };
        var enforcer = new DefaultConventionEnforcer(manifest);
        var content = "dynamic x = new ExpandoObject();";
        var violations = await enforcer.CheckAsync("test.cs", content);
        violations.Count.ShouldBe(1);
        violations[0].Rule.ShouldBe("no-dynamic");
    }

    [Fact]
    public async Task CheckAsync_TooManyLines_AndDynamic_ReturnsBoth()
    {
        var manifest = new ConventionManifest
        {
            Version = 1,
            Preset = "minimal",
            Overrides = new ConventionOverrides { MaxFileLines = 5 },
            ExtraRules = new List<ExtraRule>
            {
                new() { Rule = "no-dynamic", Severity = "warning" },
            },
        };
        var enforcer = new DefaultConventionEnforcer(manifest);
        var content = "line1\nline2\nline3\nline4\nline5\nline6\ndynamic x;";
        var violations = await enforcer.CheckAsync("test.cs", content);
        violations.Count.ShouldBe(2);
        violations.ShouldContain(v => v.Rule == "max-file-lines");
        violations.ShouldContain(v => v.Rule == "no-dynamic");
    }

    // ── YAML parser ───────────────────────────────────────────────

    [Fact]
    public void ParseMinimalYaml_Empty_ReturnsDefault()
    {
        var manifest = DefaultConventionEnforcer.ParseMinimalYaml("");
        manifest.Preset.ShouldBeNull();
        manifest.Overrides!.MaxFileLines.ShouldBeNull();
    }

    [Fact]
    public void ParseMinimalYaml_PresetOnly()
    {
        var yaml = "preset: clean-arch";
        var manifest = DefaultConventionEnforcer.ParseMinimalYaml(yaml);
        manifest.Preset.ShouldBe("clean-arch");
    }

    [Fact]
    public void ParseMinimalYaml_WithOverrides()
    {
        var yaml = @"
preset: ampelum
overrides:
  maxFileLines: 500
  maxPublicMethodsPerClass: 20
";
        var manifest = DefaultConventionEnforcer.ParseMinimalYaml(yaml);
        manifest.Preset.ShouldBe("ampelum");
        manifest.Overrides.ShouldNotBeNull();
        manifest.Overrides!.MaxFileLines.ShouldBe(500);
        manifest.Overrides.MaxPublicMethodsPerClass.ShouldBe(20);
    }

    [Fact]
    public void ParseMinimalYaml_WithExtraRules()
    {
        var yaml = @"
extraRules:
  - rule: no-dynamic
    severity: error
  - rule: required-file
    severity: warning
    path: README.md
";
        var manifest = DefaultConventionEnforcer.ParseMinimalYaml(yaml);
        manifest.ExtraRules.ShouldNotBeNull();
        manifest.ExtraRules.Count.ShouldBe(2);
        manifest.ExtraRules[0].Rule.ShouldBe("no-dynamic");
        manifest.ExtraRules[0].Severity.ShouldBe("error");
        manifest.ExtraRules[1].Rule.ShouldBe("required-file");
        manifest.ExtraRules[1].Path.ShouldBe("README.md");
    }

    [Fact]
    public void ParseMinimalYaml_WithComments_IgnoresThem()
    {
        var yaml = @"
# This is a comment
preset: minimal
# Another comment
overrides:
  maxFileLines: 600
";
        var manifest = DefaultConventionEnforcer.ParseMinimalYaml(yaml);
        manifest.Preset.ShouldBe("minimal");
        manifest.Overrides!.MaxFileLines.ShouldBe(600);
    }

    [Fact]
    public void ParseMinimalYaml_WithLayerRules()
    {
        var yaml = @"
extraRules:
  - rule: mandatory-layer
    severity: warning
    layers:
      - { name: Controllers, canUse: [Services, Domain] }
      - { name: Services, canUse: [Domain] }
      - { name: Domain, canUse: [] }
";
        var manifest = DefaultConventionEnforcer.ParseMinimalYaml(yaml);
        manifest.ExtraRules.ShouldNotBeNull();
        manifest.ExtraRules.Count.ShouldBe(1);
        manifest.ExtraRules[0].Layers.ShouldNotBeNull();
        var layers = manifest.ExtraRules[0].Layers!;
        layers.Count.ShouldBe(3);
        layers[0].Name.ShouldBe("Controllers");
        layers[0].CanUse.ShouldNotBeNull();
        layers[0].CanUse!.ShouldContain("Services");
        layers[0].CanUse!.ShouldContain("Domain");
        layers[1].Name.ShouldBe("Services");
        layers[2].Name.ShouldBe("Domain");
        layers[2].CanUse.ShouldBeEmpty();
    }

    // ── MandatoryLayerChecker ─────────────────────────────────────

    [Fact]
    public void MandatoryLayer_NoLayersConfigured_ReturnsNull()
    {
        var layers = Array.Empty<LayerRule>();
        var checker = new MandatoryLayerChecker(layers);
        var result = checker.Check("test.cs", "public class Foo { }", null);
        result.ShouldBeNull();
    }

    [Fact]
    public void MandatoryLayer_FileInValidLayer_ReturnsNull()
    {
        var layers = new List<LayerRule>
        {
            new() { Name = "Controllers", CanUse = new[] { "Services" } },
            new() { Name = "Services", CanUse = new[] { "Domain" } },
            new() { Name = "Domain", CanUse = Array.Empty<string>() },
        };
        var checker = new MandatoryLayerChecker(layers);
        var content = "public class UserController { }";
        var result = checker.Check("src/Controllers/UserController.cs", content, null);
        result.ShouldBeNull();
    }

    [Fact]
    public void MandatoryLayer_FileNotInRecognizedLayer_ReturnsViolation()
    {
        var layers = new List<LayerRule>
        {
            new() { Name = "Controllers", CanUse = new[] { "Services" } },
            new() { Name = "Services", CanUse = new[] { "Domain" } },
        };
        var checker = new MandatoryLayerChecker(layers);
        var result = checker.Check("src/Helpers/MyHelper.cs", "public class MyHelper { }", null);
        result.ShouldNotBeNull();
        result!.Rule.ShouldBe("mandatory-layer");
        result.Message.ShouldContain("not in a recognized layer");
        result.Message.ShouldContain("Controllers");
        result.Message.ShouldContain("Services");
    }

    [Fact]
    public void MandatoryLayer_WithCustomSeverity()
    {
        var layers = new List<LayerRule>
        {
            new() { Name = "Controllers", CanUse = new[] { "Services" } },
        };
        var checker = new MandatoryLayerChecker(layers);
        var ruleConfig = new ExtraRule { Rule = "mandatory-layer", Severity = "error" };
        var result = checker.Check("src/Utils/Util.cs", "public class Util { }", ruleConfig);
        result.ShouldNotBeNull();
        result!.Severity.ShouldBe("error");
    }

    [Fact]
    public void MandatoryLayer_LayerWithNoCanUse_NoViolation()
    {
        var layers = new List<LayerRule>
        {
            new() { Name = "Domain", CanUse = null },
        };
        var checker = new MandatoryLayerChecker(layers);
        var result = checker.Check("src/Domain/Entity.cs", "public class Entity { }", null);
        result.ShouldBeNull();
    }

    // ── Integration: DefaultConventionEnforcer with mandatory-layer ──────

    [Fact]
    public async Task CheckAsync_WithMandatoryLayerRule_DetectsViolation()
    {
        var manifest = new ConventionManifest
        {
            Version = 1,
            Preset = "clean-arch",
        };
        var enforcer = new DefaultConventionEnforcer(manifest);
        var content = "public class InvalidUtil { }";
        var violations = await enforcer.CheckAsync("src/Helpers/InvalidUtil.cs", content);
        violations.ShouldContain(v => v.Rule == "mandatory-layer");
    }

    [Fact]
    public async Task CheckAsync_WithMandatoryLayerRule_PassesValidLayer()
    {
        var manifest = new ConventionManifest
        {
            Version = 1,
            Preset = "clean-arch",
        };
        var enforcer = new DefaultConventionEnforcer(manifest);
        var content = "public class UserService { }";
        var violations = await enforcer.CheckAsync("src/Services/UserService.cs", content);
        violations.ShouldNotContain(v => v.Rule == "mandatory-layer");
    }

    [Fact]
    public async Task CheckAsync_MultipleViolations_ReturnsBoth()
    {
        var manifest = new ConventionManifest
        {
            Version = 1,
            Preset = "clean-arch",
        };
        var enforcer = new DefaultConventionEnforcer(manifest);
        var largeContent = string.Join("\n", Enumerable.Range(0, 600).Select(i => $"// line {i}"));
        var content = $"public class LargeService {{ {largeContent} }}";
        var violations = await enforcer.CheckAsync("src/Helpers/LargeService.cs", content);
        violations.Count.ShouldBeGreaterThanOrEqualTo(2);
        violations.ShouldContain(v => v.Rule == "max-file-lines");
        violations.ShouldContain(v => v.Rule == "mandatory-layer");
    }
}