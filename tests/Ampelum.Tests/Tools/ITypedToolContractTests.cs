using SignalGrid.Ampelum.Tools;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Tools;

public sealed class ITypedToolContractTests
{
    sealed class EchoTool : ITypedTool<string>
    {
        public string Name => "echo";
        public string Description => "Echoes Value back.";
        [FreeForm] public string Value { get; init; } = "";
        public ValueTask<string> InvokeAsync(CancellationToken ct) => ValueTask.FromResult(Value);
    }

    [Fact]
    public async Task ITypedTool_InvokeAsync_RunsWithoutFrameworkSupport()
    {
        var t = new EchoTool { Value = "hello" };
        (await t.InvokeAsync(TestContext.Current.CancellationToken)).ShouldBe("hello");
        t.Name.ShouldBe("echo");
        t.Description.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void FreeFormAttribute_TargetsPropertyAndParameter()
    {
        var u = typeof(FreeFormAttribute).GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Cast<AttributeUsageAttribute>().Single();
        u.ValidOn.HasFlag(AttributeTargets.Property).ShouldBeTrue();
        u.ValidOn.HasFlag(AttributeTargets.Parameter).ShouldBeTrue();
    }

    sealed class WriteTool : ITypedTool<int>, IPreviewable
    {
        public string Name => "write";
        public string Description => "writes";
        public ValueTask<int> InvokeAsync(CancellationToken ct) => ValueTask.FromResult(1);
        public string PreviewAsync(CancellationToken ct) => "would write";
    }

    [Fact]
    public void IPreviewable_CanBeImplementedAlongsideITypedTool()
    {
        var t = new WriteTool();
        ((IPreviewable)t).PreviewAsync(TestContext.Current.CancellationToken).ShouldBe("would write");
    }
}
