using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Observability.Phoenix;

/// <summary>
/// Pure mapper that, given a <see cref="TraceRecord"/>, populates a started
/// <see cref="Activity"/> with Phoenix / OpenInference / Arize-friendly attribute
/// aliases on top of the standard <c>gen_ai.*</c> semconv names.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class PhoenixSpanMapper
{
    /// <summary>Applies attributes to <paramref name="activity"/> from <paramref name="record"/>.</summary>
    public void Apply(Activity activity, TraceRecord record, PhoenixExporterOptions options)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(options);

        activity.SetTag("ampelum.module", record.ModuleName);
        activity.SetTag("ampelum.signature_type", record.SignatureType.Name);
        activity.SetTag("ampelum.trace_id", record.Id.ToString("D"));

        if (record.Usage is { } u)
        {
            if (u.InputTokenCount is long inTok)
            {
                activity.SetTag("gen_ai.usage.input_tokens", inTok);
                if (options.EmitOpenInferenceAliases)
                    activity.SetTag("llm.token_count.prompt", inTok);
            }
            if (u.OutputTokenCount is long outTok)
            {
                activity.SetTag("gen_ai.usage.output_tokens", outTok);
                if (options.EmitOpenInferenceAliases)
                    activity.SetTag("llm.token_count.completion", outTok);
            }
            if (u.TotalTokenCount is long totTok && options.EmitOpenInferenceAliases)
            {
                activity.SetTag("llm.token_count.total", totTok);
            }
        }

        foreach (var kv in record.Metadata)
        {
            activity.SetTag($"ampelum.{kv.Key}", kv.Value);
        }

        if (record.Error is { } err)
        {
            activity.SetStatus(ActivityStatusCode.Error, err.Message);
            activity.SetTag("exception.type", err.GetType().FullName);
            activity.SetTag("exception.message", err.Message);
            if (options.EmitOpenInferenceAliases)
            {
                activity.SetTag("openinference.span.kind", "ERROR");
            }
        }
        else if (options.EmitOpenInferenceAliases)
        {
            activity.SetTag("openinference.span.kind", "LLM");
        }

        if (options.IncludeAuditPayload)
        {
            // Each user message → gen_ai.event.user.message with content; one assistant
            // message per output. Keeps cardinality bounded by emitting events not attrs.
            foreach (var msg in record.PromptMessages)
            {
                var role = msg.Role.Value;
                activity.AddEvent(new ActivityEvent($"gen_ai.event.{role}.message",
                    tags: new ActivityTagsCollection
                    {
                        ["gen_ai.event.role"] = role,
                        ["gen_ai.event.content"] = msg.Text,
                    }));
            }
            if (!string.IsNullOrEmpty(record.RawResponse))
            {
                activity.AddEvent(new ActivityEvent("gen_ai.event.assistant.message",
                    tags: new ActivityTagsCollection
                    {
                        ["gen_ai.event.role"] = "assistant",
                        ["gen_ai.event.content"] = record.RawResponse,
                    }));
            }
        }
    }
}
