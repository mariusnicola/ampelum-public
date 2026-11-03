using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Ampelum.Samples.ComposedPrograms.Tests;

/// <summary>A logger provider that keeps every message and every scope, so a test can read what was logged.</summary>
internal sealed class RecordingLoggers : ILoggerFactory
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

    public ConcurrentQueue<(string Category, object? State)> Scopes { get; } = new();

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);

    public void Dispose()
    {
    }

    sealed class Recorder(RecordingLoggers owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            owner.Scopes.Enqueue((category, state));
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue((category, logLevel, formatter(state, exception)));
    }
}
