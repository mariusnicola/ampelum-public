using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

public sealed class DiagnosticsRetrieverTests
{
    [Fact]
    public void RetrieverQueryDuration_Exists_WithCorrectName()
    {
        Diagnostics.RetrieverQueryDuration.ShouldNotBeNull();
        Diagnostics.RetrieverQueryDuration.Name.ShouldBe("SignalGrid.Ampelum.retriever.query.duration");
    }

    [Fact]
    public void RetrieverQueryHits_Exists_WithCorrectName()
    {
        Diagnostics.RetrieverQueryHits.ShouldNotBeNull();
        Diagnostics.RetrieverQueryHits.Name.ShouldBe("SignalGrid.Ampelum.retriever.query.hits");
    }
}
