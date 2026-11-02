using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace SignalGrid.Ampelum.Host.Persistence;

/// <summary>Opens <see cref="SqliteConnection"/> instances with schema enforcement.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IHostSqliteConnectionFactory
{
    /// <summary>Opens a connection and ensures the schema is present. Not latency-gated.</summary>
    SqliteConnection Open();

    /// <summary>Opens a connection within the given latency budget; throws <see cref="SqliteOpenError"/> if exceeded.</summary>
    SqliteConnection OpenWithLatencyBudget(TimeSpan budget);
}
