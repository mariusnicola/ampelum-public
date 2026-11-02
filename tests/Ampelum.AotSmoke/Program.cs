using System.Diagnostics;
using SignalGrid.Ampelum;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

[assembly: GenerateSignatureMetadata]

[Signature("Answer the question.")]
public sealed record SmokeSig : ISignature
{
    [Input]  public required string Question { get; init; }
    [Output] public string Answer { get; init; } = "";
}

internal static class Program
{
    public static async Task<int> Main()
    {
        // Wire a process-local ActivityListener so Diagnostics.Source.StartActivity
        // returns a recording Activity — gives us a non-empty trace id to surface
        // to stdout (consumed by Pareto #19 integration test).
        string capturedTraceId = "";
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "Ampelum",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (a.OperationName == "ampelum.module.forward" && capturedTraceId.Length == 0)
                    capturedTraceId = a.TraceId.ToString();
            },
        };
        ActivitySource.AddActivityListener(listener);

        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nParis"));
        var ctx = new AmpelumContext(
            new SmokeChat(resp),
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);
        var predict = new Predict<SmokeSig>(ctx);

        var result = await predict.ForwardAsync(new SmokeSig { Question = "Capital of France?" });
        if (result.Value.Answer != "Paris")
        {
            Console.Error.WriteLine($"unexpected answer: {result.Value.Answer}");
            return 1;
        }

        // Stable, parse-friendly stdout contract consumed by Pareto19_AotBinaryRealPredict.
        Console.WriteLine($"Predict completed: {result.Value.Answer}");
        Console.WriteLine($"ampelum.trace.id={capturedTraceId}");
        Console.WriteLine("aot-smoke ok");
        return 0;
    }
}

internal sealed class SmokeChat(ChatResponse resp) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> _, ChatOptions? __ = null, CancellationToken ___ = default)
        => Task.FromResult(resp);
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> _, ChatOptions? __ = null, CancellationToken ___ = default)
        => throw new NotSupportedException();
    public object? GetService(Type _, object? __ = null) => null;
    public void Dispose() { }
}
