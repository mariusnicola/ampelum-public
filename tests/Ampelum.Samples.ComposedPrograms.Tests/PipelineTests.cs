using Ampelum.Samples.ComposedPrograms.Assistant;
using Ampelum.Samples.ComposedPrograms.Fake;
using Ampelum.Samples.ComposedPrograms.Review;
using Microsoft.Extensions.AI;
using Shouldly;
using SignalGrid.Ampelum;
using Xunit;

namespace Ampelum.Samples.ComposedPrograms.Tests;

/// <summary>The whole run: orchestrated drafting, headline, review panel, announcement and fact checks.</summary>
public sealed class PipelineTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task<(ReleaseResult Result, ScriptedModel Model)> RunAsync(Func<ReleaseNotesOptions, ReleaseNotesOptions>? tweak = null)
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options(tweak));
        return (await pipeline.RunAsync(Ct), model);
    }

    [Fact]
    public async Task Every_change_gets_one_note_in_file_order()
    {
        var (result, _) = await RunAsync();

        result.Notes.Select(n => n.Id).ShouldBe(["C-101", "C-102", "C-103", "C-104", "C-105", "C-106", "C-107", "C-108"]);
        result.Notes.Select(n => n.Kind).ShouldBe([
            ChangeKind.Feature, ChangeKind.Fix, ChangeKind.Breaking, ChangeKind.Feature,
            ChangeKind.Fix, ChangeKind.Internal, ChangeKind.Feature, ChangeKind.Breaking]);
    }

    [Fact]
    public async Task The_assembly_task_waits_for_every_area_to_be_drafted()
    {
        var (_, model) = await RunAsync();

        var calls = model.Calls.ToList();
        var lastDraftStage = calls.FindLastIndex(c => c.Mentions("Shorten the release note") || c.Mentions(Helpers.DraftPhrase));
        var firstAssembly = calls.FindIndex(c => c.Mentions("Write a headline") || c.Mentions("Review the release notes for tone"));
        firstAssembly.ShouldBeGreaterThan(lastDraftStage);
    }

    /// <summary>Hands control back before every call, so tasks that are not ordered by a dependency interleave.</summary>
    sealed class YieldingClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
    }

    [Fact]
    public async Task The_assembly_task_runs_only_after_every_area_task_even_when_calls_interleave()
    {
        await using var pipeline = new ReleaseNotesPipeline(
            new YieldingClient(ReleaseScript.Create()), Rig.Source(), Rig.Options());

        var result = await pipeline.RunAsync(Ct);

        result.Notes.Count.ShouldBe(8);
    }

    [Fact]
    public async Task The_headline_is_asked_for_twice_but_the_model_writes_it_once()
    {
        var (result, model) = await RunAsync();

        model.Count("Write a headline").ShouldBe(1);
        result.Headline.ShouldBe("8 changes, led by: Added bulk invoice export");
    }

    [Fact]
    public async Task The_two_step_adapter_extracts_the_headline_with_a_second_call()
    {
        var (_, model) = await RunAsync();

        var extraction = model.Calls.Where(c => c.User.StartsWith("Extract the following fields")).ToList();
        extraction.Count.ShouldBe(1);
        extraction[0].User.ShouldContain("Here is a headline for the release");
    }

    [Fact]
    public async Task The_panel_speaks_in_turn_and_the_publisher_closes_it()
    {
        var (result, _) = await RunAsync();

        result.Review.Speakers.ShouldBe(["Edith", "Carl", "Pia"]);
        result.Review.Approved.ShouldBeTrue();
        result.Review.Verdict.ShouldStartWith("approve");
    }

    [Fact]
    public async Task The_compliance_reviewer_reads_the_per_area_counts_from_the_tool_registry()
    {
        var (_, model) = await RunAsync();

        model.Calls.Where(c => c.Mentions("Review the release notes for tone"))
            .Any(c => c.User.Contains("Fact: changes per area: Billing=3, Editor=3, Search=2"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Breaking_notes_without_advice_make_the_panel_withhold_approval()
    {
        var (result, _) = await RunAsync(o => o with { Audiences = ["Audience: end users."] });

        result.Review.Approved.ShouldBeFalse();
        result.Review.Verdict.ShouldStartWith("revise");
    }

    [Fact]
    public async Task Later_speakers_see_what_the_earlier_ones_said()
    {
        var (_, model) = await RunAsync();

        var reviews = model.Calls.Where(c => c.Mentions("Review the release notes for tone")).ToList();
        reviews.Count.ShouldBe(3);
        reviews[1].User.ShouldContain("Edith: Tone is plain and past tense.");
        reviews[2].User.ShouldContain("Carl:");
    }

    [Fact]
    public async Task Panel_members_recall_earlier_remarks_from_the_vector_memory()
    {
        var (_, model) = await RunAsync();

        model.Calls.Where(c => c.Mentions("Review the release notes for tone"))
            .Any(c => c.User.Contains("Recalled:"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task A_model_driven_selector_chooses_the_speakers_with_extra_calls()
    {
        var (result, model) = await RunAsync(o => o with { PanelPicksSpeakerWithModel = true });

        model.Count("Pick the next speaker").ShouldBeGreaterThan(0);
        result.Review.Speakers.ShouldBe(["Edith", "Carl", "Pia"]);
    }

    [Fact]
    public async Task Episodic_memory_summarises_the_conversation_once_it_grows()
    {
        var (_, model) = await RunAsync(o => o with { EpisodicThreshold = 2, EpisodicWindow = 1 });

        model.Count("Summarize this conversation segment").ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task The_task_agent_reads_the_notes_through_its_tool_and_ends_on_the_marker()
    {
        var (result, model) = await RunAsync();

        var calls = model.Calls.Where(c => c.Mentions("You write short product announcements")).ToList();
        calls.Count.ShouldBe(2);
        calls[0].Options!.Tools!.Select(t => t.Name).ShouldContain("list_notes");
        var toolResult = calls[1].Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        toolResult.Result!.ToString()!.ShouldContain("Feature: Added bulk invoice export.");
        result.Announcement.ShouldStartWith("8 changes, led by: Added bulk invoice export.");
        result.Announcement.ShouldNotContain("TASK_COMPLETE");
    }

    [Fact]
    public async Task The_announcement_keeps_the_text_the_agent_streamed_and_ends_completed()
    {
        var model = ReleaseScript.Create();
        DraftedNote[] notes = [await Helpers.NoteAsync("C-101"), await Helpers.NoteAsync("C-102")];

        var result = await new AnnouncementWriter(Rig.Context(model)).WriteAsync("Two changes", notes, Ct);

        result.Streamed.ShouldContain("Highlights:");
        result.Streamed.ShouldContain("TASK_COMPLETE");
        result.Text.ShouldNotContain("TASK_COMPLETE");
        result.Text.ShouldStartWith("Two changes.");
        result.Status.ShouldBe(AgentStatus.Completed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task The_fact_check_stops_asking_after_its_step_limit(int steps)
    {
        // A model that keeps calling the tool and never answers.
        var model = new ScriptedModel().WhenMessage("never answers", call =>
        {
            if (!call.Mentions("Check the release note against the change record"))
                return null;
            var message = new ChatMessage(ChatRole.Assistant, "");
            message.Contents.Add(new FunctionCallContent("c", "change_details", new Dictionary<string, object?> { ["id"] = "C-103" }));
            return message;
        });
        var changes = (await Rig.Source().LoadChangesAsync(Ct)).ToDictionary(c => c.Id);
        var note = new DraftedNote("C-103", "Search", "x", ChangeKind.Breaking, 4, "Removed legacy search API v1.", 4, 0, "", 1);

        try
        {
            await new FactCheck(Rig.Context(model), changes, steps).CheckAsync(note, Ct);
        }
        catch (Exception)
        {
            // Running out of steps is allowed to be an error; only the number of questions is under test.
        }

        // The closing question that asks for an answer after the last step is one more call.
        model.Count("Check the release note against the change record").ShouldBe(steps + 1);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task The_pipeline_passes_the_fact_check_step_limit_on(int steps)
    {
        var model = new ScriptedModel().WhenMessage("never answers", call =>
        {
            if (!call.Mentions("Check the release note against the change record"))
                return null;
            var message = new ChatMessage(ChatRole.Assistant, "");
            message.Contents.Add(new FunctionCallContent("c", "change_details", new Dictionary<string, object?> { ["id"] = "C-103" }));
            return message;
        }).Include(ReleaseScript.Create());
        await using var pipeline = new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options(o => o with { FactCheckIterations = steps }));

        await Should.ThrowAsync<Exception>(() => pipeline.RunAsync(Ct));

        model.Count("Check the release note against the change record").ShouldBe(steps + 1);
    }

    [Fact]
    public async Task Only_breaking_notes_are_fact_checked_and_each_check_reads_the_change_record()
    {
        var (result, model) = await RunAsync();

        result.FactChecks.Keys.ShouldBe(["C-103", "C-108"], ignoreOrder: true);
        result.FactChecks.Values.ShouldAllBe(v => v == "consistent");
        model.Count("Check the release note against the change record").ShouldBe(4);
    }

    [Fact]
    public async Task A_note_that_does_not_match_its_record_is_reported_as_a_mismatch()
    {
        var model = ReleaseScript.Create();
        var changes = (await Rig.Source().LoadChangesAsync(Ct)).ToDictionary(c => c.Id);
        var note = new DraftedNote("C-103", "Search", "x", ChangeKind.Breaking, 4, "Improved the product.", 3, 0, "", 0);

        var verdict = await new FactCheck(Rig.Context(model), changes).CheckAsync(note, Ct);

        verdict.Value.Verdict.ShouldBe("mismatch");
    }

    [Fact]
    public async Task The_tool_lookup_answers_with_the_record_or_says_the_id_is_unknown()
    {
        var changes = (await Rig.Source().LoadChangesAsync(Ct)).ToDictionary(c => c.Id);
        var tool = new ChangeDetailsTool(changes);

        var known = await tool.InvokeAsync("""{"id":"C-101"}""", Ct);
        var unknown = await tool.InvokeAsync("""{"id":"C-999"}""", Ct);

        known.ShouldContain("Bulk invoice export");
        unknown.ShouldBe("No change with id C-999.");
    }

    [Fact]
    public async Task Fields_named_at_run_time_are_asked_for_and_returned()
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options());

        var tags = await pipeline.TagAsync(await Helpers.RowAsync("C-101"), Ct);

        tags["Component"].ShouldBe("export");
        tags["Audience"].ShouldBe("customers");
        model.Calls.Single(c => c.Mentions("Tag the change")).System.ShouldContain("Component");
    }

    [Fact]
    public async Task The_tag_fields_come_from_the_options_with_no_code_change()
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options(o => o with { TagFields = ["Audience"] }));

        var tags = await pipeline.TagAsync(await Helpers.RowAsync("C-101"), Ct);

        tags.Keys.ShouldBe(["Audience"]);
        var call = model.Calls.Single(c => c.Mentions("Tag the change"));
        call.System.ShouldContain("Audience");
        call.System.ShouldNotContain("Component");
    }
}
