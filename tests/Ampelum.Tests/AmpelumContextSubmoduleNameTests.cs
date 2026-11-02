using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class AmpelumContextSubmoduleNameTests
{
    static AmpelumContext Basic() => new(
        new FakeChatClient(), new StubAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);

    [Fact]
    public void SubmoduleName_IsNull_ByDefault() =>
        Basic().SubmoduleName.ShouldBeNull();

    [Fact]
    public void SubmoduleName_CanBeSet_ViaWithExpression_FromSameAssembly()
    {
        // The test assembly has InternalsVisibleTo; record-with init works through internal init.
        var ctx = Basic() with { SubmoduleName = "submodule-a" };
        ctx.SubmoduleName.ShouldBe("submodule-a");
    }

    sealed class StubAdapter : IAdapter
    {
        public IReadOnlyList<ChatMessage> Format(SignatureMetadata s, IReadOnlyDictionary<string, object?> i, IReadOnlyList<Example> d) => [];
        public IReadOnlyDictionary<string, object?> Parse(SignatureMetadata s, ChatResponse r) => new Dictionary<string, object?>();
    }
}
