namespace SignalGrid.Ampelum.Host;

internal sealed class NullOptimizerProgressBus : IOptimizerProgressBus
{
    public void Publish(string runId, SseEnvelope envelope) { }
    public void Complete(string runId) { }

    public async IAsyncEnumerable<SseEnvelope> Subscribe(string runId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }
}
