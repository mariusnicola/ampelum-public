using SignalGrid.Ampelum.Composition;
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Composition;

public sealed class TraceKeysTests
{
    [Fact] public void CompositionPrimitive_PinsToCompositionPrimitive() =>
        SignalGrid.Ampelum.Composition.TraceKeys.CompositionPrimitive.ShouldBe("composition.primitive");

    [Fact] public void CompositionChildCount_PinsToCompositionChildCount() =>
        SignalGrid.Ampelum.Composition.TraceKeys.CompositionChildCount.ShouldBe("composition.child_count");

    [Fact] public void CompositionChildTraceIds_PinsToCompositionChildTraceIds() =>
        SignalGrid.Ampelum.Composition.TraceKeys.CompositionChildTraceIds.ShouldBe("composition.child_trace_ids");

    [Fact] public void LoopIterations_PinsToLoopIterations() =>
        SignalGrid.Ampelum.Composition.TraceKeys.LoopIterations.ShouldBe("loop.iterations");

    [Fact] public void LoopConverged_PinsToLoopConverged() =>
        SignalGrid.Ampelum.Composition.TraceKeys.LoopConverged.ShouldBe("loop.converged");

    [Fact] public void LoopChildTraceIds_PinsToLoopChildTraceIds() =>
        SignalGrid.Ampelum.Composition.TraceKeys.LoopChildTraceIds.ShouldBe("loop.child_trace_ids");

    [Fact] public void BranchRoute_PinsToBranchRoute() =>
        SignalGrid.Ampelum.Composition.TraceKeys.BranchRoute.ShouldBe("branch.route");

    [Fact] public void BranchChildTraceIds_PinsToBranchChildTraceIds() =>
        SignalGrid.Ampelum.Composition.TraceKeys.BranchChildTraceIds.ShouldBe("branch.child_trace_ids");

    [Fact] public void SequenceLength_PinsToSequenceLength() =>
        SignalGrid.Ampelum.Composition.TraceKeys.SequenceLength.ShouldBe("sequence.length");

    [Fact] public void SequenceChildTraceIds_PinsToSequenceChildTraceIds() =>
        SignalGrid.Ampelum.Composition.TraceKeys.SequenceChildTraceIds.ShouldBe("sequence.child_trace_ids");
}
