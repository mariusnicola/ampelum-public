using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Request body of <c>POST /optimize/{moduleName}</c>. Spec §6.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record OptimizeRequest(
    string OptimizerType,
    ExampleSource TrainingSet,
    IReadOnlyDictionary<string, JsonElement> OptimizerOptions,
    BudgetHint? Budget = null);
