using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace SignalGrid.Ampelum;

/// <summary>
/// Non-generic ReAct-facing tool: a typed adapter compatible with <see cref="ReAct{TSig}"/>'s
/// LM-driven tool loop. v0.9a integration of <c>Ampelum.Tools.ITypedTool&lt;T&gt;</c>.
/// Wraps a typed tool's <c>InvokeAsync</c> behind a uniform string-result faÃ§ade so ReAct
/// can render results back into the chat without a generic type parameter on the list.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IReActTool
{
    /// <summary>Stable, machine-readable identifier shown to the LM.</summary>
    string Name { get; }

    /// <summary>Human-readable description rendered in the LM tool catalogue.</summary>
    string Description { get; }

    /// <summary>JSON schema for the tool's parameters; passed through to MEAI's <c>AIFunction</c>.</summary>
    string ParametersJsonSchema { get; }

    /// <summary>Invokes the tool with JSON-encoded arguments and returns a string result.</summary>
    ValueTask<string> InvokeAsync(string argumentsJson, CancellationToken cancellationToken);
}
