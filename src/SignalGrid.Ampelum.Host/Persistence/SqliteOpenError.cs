using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Persistence;

/// <summary>Thrown when a SQLite connection cannot be opened within the allowed latency budget.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class SqliteOpenError : Exception
{
    /// <summary>Initializes a new instance with the given message.</summary>
    public SqliteOpenError(string message) : base(message) { }

    /// <summary>Initializes a new instance with the given message and inner exception.</summary>
    public SqliteOpenError(string message, Exception inner) : base(message, inner) { }
}
