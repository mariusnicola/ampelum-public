namespace Ampelum.Samples.ComposedPrograms;

public enum ChangeKind { Feature, Fix, Breaking, Internal }

/// <summary>How the drafting step is composed for a change that deserves extra effort.</summary>
public enum DraftStrategy { BestOfN, Refine, MultiChainComparison, Audiences }

/// <summary>One row of the changes file for the release being written.</summary>
public sealed record ChangeRow(string Id, string Area, string Title, string Details);

/// <summary>A drafted, tightened and scored release note.</summary>
public sealed record DraftedNote(
    string Id,
    string Area,
    string Title,
    ChangeKind Kind,
    int Impact,
    string Note,
    int Words,
    int TightenPasses,
    string TightenStopReason,
    double Quality);

/// <summary>Everything one release run produced.</summary>
public sealed record ReleaseResult(
    IReadOnlyList<DraftedNote> Notes,
    string Headline,
    ReviewOutcome Review,
    string Announcement,
    IReadOnlyDictionary<string, string> FactChecks);

/// <summary>What the review panel concluded and how it got there.</summary>
public sealed record ReviewOutcome(IReadOnlyList<string> Speakers, string Verdict, bool Approved);
