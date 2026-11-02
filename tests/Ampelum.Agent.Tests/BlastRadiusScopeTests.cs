using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class BlastRadiusScopeTests
{
    // ── ContainsTool ────────────────────────────────────────────────

    [Fact]
    public void ContainsTool_ExactMatch_ReturnsTrue()
    {
        var scope = new BlastRadiusScope
        {
            AllowedToolNames = new HashSet<string> { "read_file", "list_directory" }
        };

        scope.ContainsTool("read_file").ShouldBeTrue();
        scope.ContainsTool("list_directory").ShouldBeTrue();
    }

    [Fact]
    public void ContainsTool_NoMatch_ReturnsFalse()
    {
        var scope = new BlastRadiusScope
        {
            AllowedToolNames = new HashSet<string> { "read_file" }
        };

        scope.ContainsTool("write_file").ShouldBeFalse();
    }

    [Fact]
    public void ContainsTool_Wildcard_ReturnsTrue()
    {
        var scope = new BlastRadiusScope
        {
            AllowedToolNames = new HashSet<string> { "*" }
        };

        scope.ContainsTool("anything").ShouldBeTrue();
        scope.ContainsTool("write_file").ShouldBeTrue();
    }

    [Fact]
    public void ContainsTool_EmptySet_ReturnsFalse()
    {
        var scope = new BlastRadiusScope(); // empty set

        scope.ContainsTool("read_file").ShouldBeFalse();
    }

    // ── ContainsPath ────────────────────────────────────────────────

    [Fact]
    public void ContainsPath_ExactPath_ReturnsTrue()
    {
        var scope = new BlastRadiusScope
        {
            AllowedResourcePaths = new HashSet<string> { "/tmp/work", "/home" }
        };

        scope.ContainsPath("/tmp/work").ShouldBeTrue();
    }

    [Fact]
    public void ContainsPath_PrefixMatch_ReturnsTrue()
    {
        var scope = new BlastRadiusScope
        {
            AllowedResourcePaths = new HashSet<string> { "/tmp/work" }
        };

        scope.ContainsPath("/tmp/work/output.txt").ShouldBeTrue();
    }

    [Fact]
    public void ContainsPath_OutsideAllowed_ReturnsFalse()
    {
        var scope = new BlastRadiusScope
        {
            AllowedResourcePaths = new HashSet<string> { "/tmp/work" }
        };

        scope.ContainsPath("/etc/passwd").ShouldBeFalse();
    }

    [Fact]
    public void ContainsPath_Wildcard_ReturnsTrue()
    {
        var scope = new BlastRadiusScope
        {
            AllowedResourcePaths = new HashSet<string> { "*" }
        };

        scope.ContainsPath("/etc/passwd").ShouldBeTrue();
        scope.ContainsPath("/any/path").ShouldBeTrue();
    }

    [Fact]
    public void ContainsPath_CaseInsensitiveMatch_ReturnsTrue()
    {
        var scope = new BlastRadiusScope
        {
            AllowedResourcePaths = new HashSet<string> { "/Tmp/Work" }
        };

        scope.ContainsPath("/tmp/work/readme.md").ShouldBeTrue();
    }

    // ── MaxDangerTier ───────────────────────────────────────────────

    [Fact]
    public void MaxAllowedTier_DefaultsToDestructive()
    {
        var scope = new BlastRadiusScope();

        scope.MaxAllowedTier.ShouldBe(DangerTier.Destructive);
    }

    // ── EscalationContext ───────────────────────────────────────────

    [Fact]
    public void EscalationContext_Constructor_SetsProperties()
    {
        var ctx = new EscalationContext(
            Type: EscalationType.BlastRadiusBreach,
            ToolName: "write_file",
            Reason: "Path not in allowed scope",
            OccurredAt: DateTimeOffset.UtcNow,
            Metadata: "/etc/passwd"
        );

        ctx.Type.ShouldBe(EscalationType.BlastRadiusBreach);
        ctx.ToolName.ShouldBe("write_file");
        ctx.Reason.ShouldBe("Path not in allowed scope");
        ctx.Metadata.ShouldBe("/etc/passwd");
    }
}