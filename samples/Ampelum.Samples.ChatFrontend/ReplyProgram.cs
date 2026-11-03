using System.Runtime.CompilerServices;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Streaming;

namespace Ampelum.Samples.ChatFrontend;

/// <summary>A program whose typed fields arrive one after the other, as the widget draws them.</summary>
public sealed class ReplyProgram(Predict<ReplySignature> predict, string question) : IStreamableProgram<ReplySignature>
{
    /// <summary>The field the widget shows first; <c>ProgramStreamer</c> records it on its span.</summary>
    public const string PrimaryField = nameof(ReplySignature.Answer);

    public async IAsyncEnumerable<StreamUpdate<ReplySignature>> StreamAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var update in predict.StreamAsync(new ReplySignature { Question = question }, ct: ct))
            yield return new StreamUpdate<ReplySignature>(update.FieldName, update.PartialText, update.IsFinal, update.Value);
    }
}
