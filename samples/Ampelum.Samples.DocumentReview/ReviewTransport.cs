namespace Ampelum.Samples.DocumentReview;

/// <summary>
/// What the desk talks through: one message handler per remote service, the clock the poll loop waits on,
/// and the addresses the offline run overrides. A null address leaves the library default in place.
/// </summary>
internal sealed record ReviewTransport(
    HttpMessageHandler Rerank,
    HttpMessageHandler Agent,
    HttpMessageHandler Batch,
    TimeProvider Clock,
    Uri? CohereEndpoint,
    Uri? VoyageEndpoint,
    Uri? BatchBase)
{
    public static ReviewTransport Live() => new(
        new SocketsHttpHandler(), new SocketsHttpHandler(), new SocketsHttpHandler(), TimeProvider.System, null, null, null);
}
