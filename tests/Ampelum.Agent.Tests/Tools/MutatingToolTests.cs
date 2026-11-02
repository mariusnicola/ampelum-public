using Shouldly;
using SignalGrid.Ampelum.Agent;
using Xunit;

namespace SignalGrid.Ampelum.Tests.Tools;

public sealed class MutatingToolTests
{
    /// <summary>Concrete test implementation of MutatingTool.</summary>
    private sealed class TestMutatingTool : MutatingTool<TestParams>
    {
        public bool CoreWasCalled { get; private set; }

        public TestMutatingTool(ITokenStore tokenStore, AgentIdentity session)
            : base(tokenStore, session)
        {
        }

        protected override Task<ToolResult> ExecuteCoreAsync(TestParams parameters, CancellationToken ct = default)
        {
            CoreWasCalled = true;
            return Task.FromResult(new ToolResult("test", true, "success", null, DateTimeOffset.UtcNow, TimeSpan.Zero));
        }
    }

    /// <summary>Test parameter type implementing IHasObservationToken.</summary>
    private sealed record TestParams(string? Token) : IHasObservationToken;

    private static (InMemoryTokenStore store, AgentIdentity identity) CreateSession()
    {
        var store = new InMemoryTokenStore();
        var identity = new AgentIdentity(
            "test-agent",
            "session-abc",
            [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1));
        return (store, identity);
    }

    private static ObservationToken MakeToken(string tokenId, string sessionId = "session-abc", int expiresAtIndex = 100)
    {
        return new ObservationToken(tokenId, "/path/file.txt", "read_file", sessionId, 1, expiresAtIndex);
    }

    [Fact]
    public async Task ExecuteAsync_ValidToken_CallsExecuteCore()
    {
        // Arrange
        var (store, identity) = CreateSession();
        store.Store(MakeToken("token-1"));
        var tool = new TestMutatingTool(store, identity);

        // Act
        var result = await tool.ExecuteAsync(new TestParams("token-1"), TestContext.Current.CancellationToken);

        // Assert
        result.Success.ShouldBeTrue();
        tool.CoreWasCalled.ShouldBeTrue();
        result.Output.ShouldBe("success");
    }

    [Fact]
    public async Task ExecuteAsync_NullToken_ReturnsBlocked()
    {
        // Arrange
        var (store, identity) = CreateSession();
        var tool = new TestMutatingTool(store, identity);

        // Act
        var result = await tool.ExecuteAsync(new TestParams(null), TestContext.Current.CancellationToken);

        // Assert
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("required");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyToken_ReturnsBlocked()
    {
        // Arrange
        var (store, identity) = CreateSession();
        var tool = new TestMutatingTool(store, identity);

        // Act
        var result = await tool.ExecuteAsync(new TestParams(""), TestContext.Current.CancellationToken);

        // Assert
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("required");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownTokenId_ReturnsBlocked()
    {
        // Arrange
        var (store, identity) = CreateSession();
        var tool = new TestMutatingTool(store, identity);

        // Act
        var result = await tool.ExecuteAsync(new TestParams("nonexistent-token"), TestContext.Current.CancellationToken);

        // Assert
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("invalid");
    }

    [Fact]
    public async Task ExecuteAsync_SessionMismatch_ReturnsBlocked()
    {
        // Arrange
        var (store, identity) = CreateSession();
        store.Store(MakeToken("token-2", sessionId: "different-session"));
        var tool = new TestMutatingTool(store, identity);

        // Act
        var result = await tool.ExecuteAsync(new TestParams("token-2"), TestContext.Current.CancellationToken);

        // Assert
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("different session");
    }

    [Fact]
    public async Task ExecuteAsync_ExpiredToken_ReturnsBlocked()
    {
        // Arrange
        var (store, identity) = CreateSession();
        // Token expires at index 0, advance CallIndex beyond it
        var expiredToken = new ObservationToken("expired", "/path", "read_file", identity.InstanceId, 0, 0);
        store.Store(expiredToken);
        store.NextCallIndex(); // Bump CallIndex to 1, exceeds ExpiresAtCallIndex of 0

        var tool = new TestMutatingTool(store, identity);

        // Act
        var result = await tool.ExecuteAsync(new TestParams("expired"), TestContext.Current.CancellationToken);

        // Assert
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("expired");
    }

    [Fact]
    public async Task ExecuteAsync_NullParameters_Throws()
    {
        // Arrange
        var (store, identity) = CreateSession();
        var tool = new TestMutatingTool(store, identity);

        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(() =>
            tool.ExecuteAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Constructor_NullTokenStore_Throws()
    {
        // Arrange
        var identity = new AgentIdentity("agent", "inst", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => new TestMutatingTool(null!, identity));
    }

    [Fact]
    public void Constructor_NullSession_Throws()
    {
        // Arrange
        var store = new InMemoryTokenStore();

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => new TestMutatingTool(store, null!));
    }

    [Fact]
    public async Task ExecuteAsync_ValidToken_PassesCancellationTokenToCoreAsync()
    {
        // Arrange
        var (store, identity) = CreateSession();
        store.Store(MakeToken("token-3"));

        var cancellationTokenReceived = false;
        var testCts = new CancellationTokenSource();

        // Create a custom tool that respects the cancellation token
        var customTool = new CustomTestMutatingTool(store, identity, (_, ct) =>
        {
            cancellationTokenReceived = ct == testCts.Token;
            return Task.FromResult(new ToolResult("test", true, "ok", null, DateTimeOffset.UtcNow, TimeSpan.Zero));
        });

        // Act
        var result = await customTool.ExecuteAsync(
            new TestParams("token-3"),
            testCts.Token);

        // Assert
        cancellationTokenReceived.ShouldBeTrue();
        result.Success.ShouldBeTrue();
    }

    /// <summary>Test tool that captures the CancellationToken passed to ExecuteCoreAsync.</summary>
    private sealed class CustomTestMutatingTool : MutatingTool<TestParams>
    {
        private readonly Func<TestParams, CancellationToken, Task<ToolResult>> _handler;

        public CustomTestMutatingTool(
            ITokenStore tokenStore,
            AgentIdentity session,
            Func<TestParams, CancellationToken, Task<ToolResult>> handler)
            : base(tokenStore, session)
        {
            _handler = handler;
        }

        protected override Task<ToolResult> ExecuteCoreAsync(TestParams parameters, CancellationToken ct = default)
        {
            return _handler(parameters, ct);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ValidToken_DoesNotCallCoreWhenExpired()
    {
        // Arrange
        var (store, identity) = CreateSession();
        var expiredToken = new ObservationToken("tok", "/path", "read_file", identity.InstanceId, 0, 0);
        store.Store(expiredToken);
        // Advance well past expiry
        for (int i = 0; i < 3; i++)
        {
            store.NextCallIndex();
        }

        var tool = new TestMutatingTool(store, identity);

        // Act
        var result = await tool.ExecuteAsync(new TestParams("tok"), TestContext.Current.CancellationToken);

        // Assert
        tool.CoreWasCalled.ShouldBeFalse();
        result.Success.ShouldBeFalse();
    }

    [Fact]
    public void IHasObservationToken_TestParams_ImplementsInterface()
    {
        // Arrange & Act
        IHasObservationToken param = new TestParams("my-token");

        // Assert
        param.Token.ShouldBe("my-token");
    }

    [Fact]
    public async Task ExecuteAsync_ValidToken_CoreResultPropagated()
    {
        // Arrange
        var (store, identity) = CreateSession();
        store.Store(MakeToken("t"));
        var tool = new TestMutatingTool(store, identity);

        // Act
        var result = await tool.ExecuteAsync(new TestParams("t"), TestContext.Current.CancellationToken);

        // Assert
        result.Output.ShouldBe("success");
        result.ToolName.ShouldBe("test");
    }

    [Fact]
    public async Task ExecuteAsync_BlockedResult_HasEmptyToolName()
    {
        // Arrange
        var (store, identity) = CreateSession();
        var tool = new TestMutatingTool(store, identity);

        // Act
        var result = await tool.ExecuteAsync(new TestParams(null), TestContext.Current.CancellationToken);

        // Assert
        result.ToolName.ShouldBe("");
        result.Success.ShouldBeFalse();
    }
}
