using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum;

/// <summary>Base record for all events emitted by an <see cref="ITaskAgent"/>.</summary>
[JsonDerivedType(typeof(TokenEvent), typeDiscriminator: "token")]
[JsonDerivedType(typeof(ToolCallEvent), typeDiscriminator: "tool_call")]
[JsonDerivedType(typeof(ToolResultEvent), typeDiscriminator: "tool_result")]
[JsonDerivedType(typeof(StatusEvent), typeDiscriminator: "status")]
[JsonDerivedType(typeof(ErrorEvent), typeDiscriminator: "error")]
[JsonDerivedType(typeof(CompleteEvent), typeDiscriminator: "complete")]
public abstract record AgentEvent(string Type);

/// <summary>Emitted when the agent produces a text token in its response.</summary>
/// <param name="Text">The token text content.</param>
public sealed record TokenEvent(string Text) : AgentEvent("token");

/// <summary>Emitted when the agent invokes a tool.</summary>
/// <param name="Tool">The name of the tool being called.</param>
/// <param name="Args">The JSON arguments passed to the tool, if any.</param>
public sealed record ToolCallEvent(string Tool, string? Args) : AgentEvent("tool_call");

/// <summary>Emitted when a tool invocation completes with a result or error.</summary>
/// <param name="Tool">The name of the tool that produced the result.</param>
/// <param name="Result">The result content, if successful.</param>
/// <param name="IsError">Whether the tool invocation produced an error.</param>
public sealed record ToolResultEvent(string Tool, string? Result, bool IsError) : AgentEvent("tool_result");

/// <summary>Emitted when the agent's status changes.</summary>
/// <param name="Status">The new agent status.</param>
/// <param name="Message">Optional status message.</param>
public sealed record StatusEvent(AgentStatus Status, string? Message) : AgentEvent("status");

/// <summary>Emitted when the agent encounters a non-recoverable error.</summary>
/// <param name="Code">A machine-readable error code.</param>
/// <param name="Message">A human-readable error description.</param>
public sealed record ErrorEvent(string Code, string Message) : AgentEvent("error");

/// <summary>Emitted when the agent completes its task.</summary>
/// <param name="Summary">A summary of the completed task.</param>
/// <param name="InputTokens">Number of input tokens consumed.</param>
/// <param name="OutputTokens">Number of output tokens produced.</param>
/// <param name="Cost">Estimated cost in USD, if available.</param>
public sealed record CompleteEvent(string Summary, long InputTokens, long OutputTokens, decimal? Cost) : AgentEvent("complete");
