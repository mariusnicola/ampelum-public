using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tools.Tests;

public sealed class EditToolTests
{
    [Fact]
    public void Execute_Returns_Formatted_String_With_FilePath_And_Identity()
    {
        var tool = new EditTool();
        var identity = new AgentIdentity(
            "Artisan",
            "test-agent-1",
            ["*"],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1));

        var result = tool.Execute("/path/to/file.cs", "old", "new", identity);

        result.ShouldBe("Edited /path/to/file.cs with identity test-agent-1");
    }
}
