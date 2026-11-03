using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Safety.Moderation;
using SignalGrid.Ampelum.Safety.Policy;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Tests.Safety;

/// <summary>
/// All after hooks run even when one throws, so a hook that blocks a reply must take the reply out of the
/// outputs first: the audit hook still records the call, without the withheld text.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class BlockedReplyAuditTests
{
    const string Flagged = "FLAGGED-REPLY-TEXT";

    [Signature("Answer the question.")]
    public sealed record Ask : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    sealed class Moderator(Func<ModerationResult> verdict) : IOutputModerator
    {
        public ValueTask<ModerationResult> ModerateAsync(string text, CancellationToken cancellationToken) => new(verdict());
    }

    static async Task<(Exception? Error, List<AuditEntry> Entries)> RunAsync(params IHook[] guards)
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = new InMemoryAuditSink();
        var chat = new FakeChatClient().EnqueueText($"[[ ## Answer ## ]]\n{Flagged}\n[[ ## completed ## ]]");
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
            .WithHook(new AuditHook(sink));
        foreach (var g in guards) ctx = ctx.WithHook(g);

        Exception? error = null;
        try { await new Predict<Ask>(ctx).ForwardAsync(new Ask { Question = "q" }, ct: ct); }
        catch (Exception ex) { error = ex; }

        var entries = new List<AuditEntry>();
        await foreach (var e in sink.EnumerateAsync(ct)) entries.Add(e);
        return (error, entries);
    }

    static void ShouldHoldNoReply(List<AuditEntry> entries)
    {
        entries.Count.ShouldBe(1);
        entries[0].Outputs.ShouldBeEmpty();
        string.Join('\n', entries.SelectMany(e => e.Inputs.Values.Concat(e.Outputs.Values)).Select(v => v?.ToString()))
            .ShouldNotContain(Flagged);
    }

    [Fact]
    public async Task A_flagged_reply_is_audited_without_its_text()
    {
        var (error, entries) = await RunAsync(new ModerationHook(
            new Moderator(() => new ModerationResult(true, ["violence"], null))));

        error.ShouldBeOfType<ModerationException>();
        ShouldHoldNoReply(entries);
    }

    [Fact]
    public async Task A_reply_moderation_could_not_check_is_audited_without_its_text()
    {
        var (error, entries) = await RunAsync(new ModerationHook(
            new Moderator(() => throw new HttpRequestException("moderation unreachable")), failOpen: false));

        error.ShouldBeOfType<HttpRequestException>();
        ShouldHoldNoReply(entries);
    }

    [Fact]
    public async Task A_reply_the_content_policy_rejects_is_audited_without_its_text()
    {
        var policy = new ContentPolicyBuilder().Reject("no-flagged", new Regex(Flagged)).Build();

        var (error, entries) = await RunAsync(new ContentPolicyHook(policy));

        error.ShouldBeOfType<ContentPolicyViolationException>();
        ShouldHoldNoReply(entries);
    }

    [Fact]
    public async Task A_flagged_streamed_reply_is_audited_without_its_text()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = new InMemoryAuditSink();
        var chat = new FakeChatClient().EnqueueStreamingChunks("FLAGGED-", "REPLY-TEXT");
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
            .WithHook(new AuditHook(sink))
            .WithHook(new ModerationHook(new Moderator(() => new ModerationResult(true, ["violence"], null))));
        var session = new StreamingChatSession(ctx, "Sys");

        await Should.ThrowAsync<ModerationException>(async () =>
        {
            await foreach (var _ in session.SendAsync("hi", ct)) { }
        });

        var entries = new List<AuditEntry>();
        await foreach (var e in sink.EnumerateAsync(ct)) entries.Add(e);
        ShouldHoldNoReply(entries);
    }

    [Fact]
    public async Task A_reply_that_passes_moderation_is_audited_with_its_text()
    {
        var (error, entries) = await RunAsync(new ModerationHook(
            new Moderator(() => new ModerationResult(false, [], null))));

        error.ShouldBeNull();
        entries.Count.ShouldBe(1);
        entries[0].Outputs["Answer"].ShouldBe(Flagged);
    }

    [Fact]
    public async Task A_reply_moderation_could_not_check_is_kept_when_it_fails_open()
    {
        var (error, entries) = await RunAsync(new ModerationHook(
            new Moderator(() => throw new HttpRequestException("moderation unreachable")), failOpen: true));

        error.ShouldBeNull();
        entries[0].Outputs["Answer"].ShouldBe(Flagged);
    }
}
