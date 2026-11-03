using SignalGrid.Ampelum.Safety.Moderation;

namespace Ampelum.Samples.SafetyGateway;

/// <summary>
/// Wraps the moderator so an outage leaves a note in the audit chain. The hook still fails closed:
/// the error is rethrown and the reply is not released.
/// </summary>
public sealed class AuditedModerator(IOutputModerator inner) : IOutputModerator
{
    /// <summary>Set once the audit hook exists, which is after the moderator is built.</summary>
    public SafetyNotes? Notes { get; set; }

    public async ValueTask<ModerationResult> ModerateAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            return await inner.ModerateAsync(text, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller asked to stop; that is a cancellation, not an outage.
            throw;
        }
        catch (Exception ex)
        {
            if (Notes is not null)
            {
                await Notes.RecordAsync(SafetyNotes.ModerationUnavailable,
                    new Dictionary<string, object?> { ["error"] = ex.GetType().Name }, cancellationToken);
            }
            // A timeout and an unreadable answer are outages too, so the caller handles one exception type.
            throw new ModerationUnavailableException(ex);
        }
    }
}

/// <summary>The moderation service could not give a usable answer, so the reply was not released.</summary>
public sealed class ModerationUnavailableException(Exception inner)
    : Exception($"Moderation unavailable ({inner.GetType().Name}); the reply was withheld.", inner);
