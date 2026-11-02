using System.Text.Json;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests.Mcp;

public sealed class McpServerAdapterTests
{
    // ── Stub implementations ────────────────────────────────────────

    private sealed class StubToolLoader : IToolLoader
    {
        private readonly List<ToolSurvey> _surveys = [];

        public StubToolLoader(params ToolSurvey[] surveys)
        {
            _surveys.AddRange(surveys);
        }

        public Task<IReadOnlyList<ToolSurvey>> SurveyAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ToolSurvey>>(_surveys.AsReadOnly());

        public Task<ToolManifest> UnpackAsync(string toolName, CancellationToken ct = default)
            => throw new KeyNotFoundException($"Tool not found: {toolName}");

        public Task<ToolManifest?> TryUnpackAsync(string toolName, CancellationToken ct = default)
            => Task.FromResult<ToolManifest?>(null);

        public ValueTask EvictAsync(string toolName)
            => ValueTask.CompletedTask;

        public Task<IReadOnlyList<ToolSurvey>> SearchSurveyAsync(string query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ToolSurvey>>([]);
    }

    private sealed class StubToolDispatcher : IToolDispatcher
    {
        public Task<ToolResult> InvokeAsync(
            string toolName,
            IReadOnlyDictionary<string, JsonElement> args,
            AgentIdentity caller,
            CancellationToken ct = default)
        {
            var result = new ToolResult(
                toolName,
                Success: true,
                Output: $"Invoked {toolName}",
                Error: null,
                ExecutedAt: DateTimeOffset.UtcNow,
                Duration: TimeSpan.Zero);
            return Task.FromResult(result);
        }
    }

    // ── Tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task HandleToolsListAsync_ReturnsAllSurveys()
    {
        var surveys = new[]
        {
            new ToolSurvey { Name = "edit_file", Description = "Edits a file", DangerTier = DangerTier.Additive, RequiresConfirmation = false },
            new ToolSurvey { Name = "delete_file", Description = "Deletes a file", DangerTier = DangerTier.Mutating, RequiresConfirmation = true }
        };
        var loader = new StubToolLoader(surveys);
        var dispatcher = new StubToolDispatcher();
        var options = new McpServerOptions("test-server");
        var adapter = new McpServerAdapter(loader, dispatcher, options);

        var result = await adapter.HandleToolsListAsync(TestContext.Current.CancellationToken);

        result.Count.ShouldBe(2);
        result[0].Name.ShouldBe("edit_file");
        result[1].Name.ShouldBe("delete_file");
    }

    [Fact]
    public async Task HandleToolsCallAsync_InvokesDispatcher_AndReturnsResult()
    {
        var loader = new StubToolLoader();
        var dispatcher = new StubToolDispatcher();
        var options = new McpServerOptions("test-server");
        var adapter = new McpServerAdapter(loader, dispatcher, options);

        var identity = new AgentIdentity("test-agent", "inst-123", ["Safe"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var args = new Dictionary<string, object?> { { "path", "/tmp/file.txt" } };

        var result = await adapter.HandleToolsCallAsync("edit_file", args, identity, TestContext.Current.CancellationToken);

        result.ToolName.ShouldBe("edit_file");
        result.Success.ShouldBeTrue();
        result.Output?.ShouldContain("Invoked edit_file");
    }

    [Fact]
    public async Task HandleToolsCallAsync_ConvertsPrimitiveArgs_ToJsonElements()
    {
        // This test verifies that primitive args are correctly converted to JsonElement
        var loader = new StubToolLoader();
        var dispatcher = new StubToolDispatcher();
        var options = new McpServerOptions("test-server");
        var adapter = new McpServerAdapter(loader, dispatcher, options);

        var identity = new AgentIdentity("test-agent", "inst-123", ["Safe"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var args = new Dictionary<string, object?>
        {
            { "str_arg", "hello" },
            { "int_arg", 42 },
            { "bool_arg", true },
            { "null_arg", null }
        };

        var result = await adapter.HandleToolsCallAsync("edit_file", args, identity, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
    }
}
