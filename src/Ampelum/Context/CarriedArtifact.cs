using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Context;

/// <summary>Closed taxonomy of artifacts carried across mode transitions.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "`$type")]
[JsonDerivedType(typeof(CarriedPlan), "carried_plan")]
[JsonDerivedType(typeof(CarriedDecisions), "carried_decisions")]
[JsonDerivedType(typeof(CarriedFiles), "carried_files")]
[JsonDerivedType(typeof(CarriedTestResults), "carried_test_results")]
public abstract record CarriedArtifact(string Kind);

/// <summary>Carried plan items across a mode transition.</summary>
public sealed record CarriedPlan(string Kind, string PlanId, string Title, string Status, IReadOnlyList<PlanItemSnapshot> Items) : CarriedArtifact(Kind);

/// <summary>Snapshot of a plan item at the time of transition.</summary>
public sealed record PlanItemSnapshot(string Id, string Title, string Status);

/// <summary>Key decisions carried across a mode transition.</summary>
public sealed record CarriedDecisions(string Kind, IReadOnlyList<string> DecisionStatements) : CarriedArtifact(Kind);

/// <summary>File paths carried across a mode transition.</summary>
public sealed record CarriedFiles(string Kind, IReadOnlyList<string> FilePaths) : CarriedArtifact(Kind);

/// <summary>Test results carried across a mode transition.</summary>
public sealed record CarriedTestResults(string Kind, IReadOnlyList<TestResultSummary> Results) : CarriedArtifact(Kind);

/// <summary>Summary of a single test result.</summary>
public sealed record TestResultSummary(string TestName, bool Passed, string Message);
