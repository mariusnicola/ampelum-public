using System.Text;
using SignalGrid.Ampelum.Agent;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>Turns the stored assessments into the markdown the report tool writes.</summary>
public static class ReportComposer
{
    /// <summary>The draft handed to the editor loop: findings only.</summary>
    public static string Draft(AssessDetail assessed) => Compose([], assessed, []);

    /// <summary>
    /// The report. <paramref name="notes"/> are the durable phase notes: the goal comes first and the finished phases follow,
    /// so a report written after a restart still states what the run was for.
    /// </summary>
    public static string Compose(IReadOnlyList<PhaseContextMessage> notes, AssessDetail assessed, IReadOnlyList<string> editorNotes)
    {
        var text = new StringBuilder();
        text.AppendLine("# Triage report").AppendLine();
        foreach (var note in notes)
            text.AppendLine(note.Content).AppendLine();

        foreach (var result in assessed.Results)
        {
            text.AppendLine($"## #{result.Issue} {result.Title}")
                .AppendLine($"- Severity: {result.Severity}")
                .AppendLine($"- Cause: {result.Cause}")
                .AppendLine($"- Next step: {result.NextStep}")
                .AppendLine($"- Skill: {result.Skill} (found by {result.SkillRoute})")
                .AppendLine($"- Brief hash: {result.BriefHash}");
            foreach (var violation in result.Violations)
                text.AppendLine($"- Convention: {violation}");
            text.AppendLine();
        }

        if (assessed.Deferred.Count > 0)
        {
            text.AppendLine("## Deferred");
            foreach (var deferred in assessed.Deferred)
                text.AppendLine($"- #{deferred.Issue}: {deferred.Reason}");
            text.AppendLine();
        }

        if (editorNotes.Count > 0)
        {
            text.AppendLine("## Editor notes");
            foreach (var note in editorNotes)
                text.AppendLine($"- {note}");
        }
        return text.ToString();
    }
}
