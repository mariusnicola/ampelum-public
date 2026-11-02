#pragma warning disable AMPELUM_V09_PREVIEW
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Hooks;
using Shouldly;
using Xunit;
using SignalGrid.Ampelum.Host.Replay;

namespace SignalGrid.Ampelum.Host.Tests.Replay;

public sealed class AuditReplayHookTests
{
    [Fact]
    public void Order_Is_Minus1000()
    {
        var entry = CreateTestEntry();
        var hash = AuditChainHasher.ComputeReplay(
            entry.ModuleName, entry.EventType, entry.ModelId, entry.Inputs);
        var hook = new AuditReplayHook(entry, hash);
        hook.Order.ShouldBe(-1000);
    }

    [Fact]
    public async Task OnBeforeAsync_HashMatch_SetsOutputs()
    {
        var entry = CreateTestEntry();
        var hash = AuditChainHasher.ComputeReplay(
            entry.ModuleName, entry.EventType, entry.ModelId, entry.Inputs);
        var hook = new AuditReplayHook(entry, hash);

        var ctx = new HookContext
        {
            ModuleName = entry.ModuleName,
            Inputs = entry.Inputs,
        };

        await hook.OnBeforeAsync(ctx, TestContext.Current.CancellationToken);

        ctx.Outputs.ShouldBeSameAs(entry.Outputs);
    }

    [Fact]
    public async Task OnBeforeAsync_HashMismatch_Throws()
    {
        var entry = CreateTestEntry();
        var wrongHash = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef";
        var hook = new AuditReplayHook(entry, wrongHash);

        var ctx = new HookContext
        {
            ModuleName = entry.ModuleName,
            Inputs = entry.Inputs,
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => hook.OnBeforeAsync(ctx, TestContext.Current.CancellationToken).AsTask());
        ex.Message.ShouldContain("Replay hash mismatch");
        ex.Message.ShouldContain("deadbeef");
    }

    [Fact]
    public async Task OnAfterAsync_ZeroTokens_DoesNotThrow()
    {
        var entry = CreateTestEntry();
        var hash = AuditChainHasher.ComputeReplay(
            entry.ModuleName, entry.EventType, entry.ModelId, entry.Inputs);
        var hook = new AuditReplayHook(entry, hash);

        var ctx = new HookContext
        {
            ModuleName = entry.ModuleName,
            Inputs = entry.Inputs,
            Outputs = entry.Outputs,
            PromptTokens = 0,
            CompletionTokens = 0,
        };

        // Should not throw
        await hook.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OnAfterAsync_NonZeroPromptTokens_ThrowsReplayLmCalledException()
    {
        var entry = CreateTestEntry();
        var hash = AuditChainHasher.ComputeReplay(
            entry.ModuleName, entry.EventType, entry.ModelId, entry.Inputs);
        var hook = new AuditReplayHook(entry, hash);

        var ctx = new HookContext
        {
            ModuleName = entry.ModuleName,
            Inputs = entry.Inputs,
            Outputs = entry.Outputs,
            PromptTokens = 10,
            CompletionTokens = 0,
        };

        var ex = await Should.ThrowAsync<ReplayLmCalledException>(
            () => hook.OnAfterAsync(ctx, TestContext.Current.CancellationToken).AsTask());
        ex.Message.ShouldContain("short-circuit failed");
        ex.Message.ShouldContain("prompt=10");
    }

    [Fact]
    public async Task OnAfterAsync_NonZeroCompletionTokens_ThrowsReplayLmCalledException()
    {
        var entry = CreateTestEntry();
        var hash = AuditChainHasher.ComputeReplay(
            entry.ModuleName, entry.EventType, entry.ModelId, entry.Inputs);
        var hook = new AuditReplayHook(entry, hash);

        var ctx = new HookContext
        {
            ModuleName = entry.ModuleName,
            Inputs = entry.Inputs,
            Outputs = entry.Outputs,
            PromptTokens = 0,
            CompletionTokens = 5,
        };

        var ex = await Should.ThrowAsync<ReplayLmCalledException>(
            () => hook.OnAfterAsync(ctx, TestContext.Current.CancellationToken).AsTask());
        ex.Message.ShouldContain("short-circuit failed");
        ex.Message.ShouldContain("completion=5");
    }

    [Fact]
    public async Task OnErrorAsync_DoesNotThrow()
    {
        var entry = CreateTestEntry();
        var hash = AuditChainHasher.ComputeReplay(
            entry.ModuleName, entry.EventType, entry.ModelId, entry.Inputs);
        var hook = new AuditReplayHook(entry, hash);

        var ctx = new HookContext
        {
            ModuleName = entry.ModuleName,
            Inputs = entry.Inputs,
        };
        var error = new InvalidOperationException("test");

        // Should not throw
        await hook.OnErrorAsync(ctx, error, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Constructor_NullEntry_Throws()
    {
        var ex = Should.Throw<ArgumentNullException>(
            () => new AuditReplayHook(null!, "hash"));
        ex.ParamName.ShouldBe("entry");
    }

    [Fact]
    public void Constructor_EmptyHash_Throws()
    {
        var entry = CreateTestEntry();
        var ex = Should.Throw<ArgumentException>(
            () => new AuditReplayHook(entry, ""));
        ex.ParamName.ShouldBe("expectedReplayHash");
    }

    [Fact]
    public void Constructor_NullHash_Throws()
    {
        var entry = CreateTestEntry();
        var ex = Should.Throw<ArgumentException>(
            () => new AuditReplayHook(entry, null!));
        ex.ParamName.ShouldBe("expectedReplayHash");
    }

    static AuditEntry CreateTestEntry()
    {
        var inputs = new Dictionary<string, object?> { ["x"] = 1 };
        var outputs = new Dictionary<string, object?> { ["result"] = "test" };
        return new AuditEntry
        {
            Sequence = 1,
            Timestamp = DateTimeOffset.UtcNow,
            ModuleName = "TestModule",
            ModelId = "test-model",
            EventType = "test.event",
            Inputs = inputs,
            Outputs = outputs,
            PromptTokens = 10,
            CompletionTokens = 5,
            PrevHash = new byte[32],
            EntryHash = new byte[32],
        };
    }
}
