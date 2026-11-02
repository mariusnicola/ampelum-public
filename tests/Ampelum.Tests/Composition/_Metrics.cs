namespace Ampelum.Tests.Composition._Metrics;

using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Metrics;
using Ampelum.Tests.Composition._Sigs;

internal sealed class AlphabeticalMetric : IMetric<TestSig>
{
    public double Score(TestSig prediction, TestSig inputs, SignatureMetadata signature)
        => string.IsNullOrEmpty(prediction.Answer) ? 0.0 : 1.0 / (1 + prediction.Answer[0]);
}
