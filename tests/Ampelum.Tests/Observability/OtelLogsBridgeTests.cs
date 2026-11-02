using System.Diagnostics;
using SignalGrid.Ampelum.Observability;
using SignalGrid.Ampelum;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Observability;

#pragma warning disable AMPELUM_V09_PREVIEW

[Collection("OtelLogsBridge sequential")]
public sealed class OtelLogsBridgeTests
{
    sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<object> Scopes { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            Scopes.Add(state);
            return new Disposer();
        }
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) { }
        sealed class Disposer : IDisposable { public void Dispose() { } }
    }

    sealed class ActivityScope : IDisposable
    {
        public Activity Activity { get; }
        readonly ActivitySource _src;
        readonly ActivityListener _listener;
        public ActivityScope()
        {
            _src = new ActivitySource("ampelum-otel-logs-tests-" + Guid.NewGuid());
            _listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == _src.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            };
            ActivitySource.AddActivityListener(_listener);
            Activity = _src.StartActivity("test", ActivityKind.Internal)!;
        }
        public void Dispose()
        {
            Activity?.Stop();
            _listener.Dispose();
            _src.Dispose();
        }
    }

    [Fact]
    public void BeginGenAiScope_ReturnsNullWhenNoActivity()
    {
        var prior = Activity.Current;
        Activity.Current = null;
        try
        {
            var logger = new CapturingLogger();
            OtelLogsBridge.BeginGenAiScope(logger).ShouldBeNull();
            logger.Scopes.ShouldBeEmpty();
        }
        finally { Activity.Current = prior; }
    }

    [Fact]
    public void BeginGenAiScope_ReturnsNullWhenActivityCarriesNoGenAiTags()
    {
        using var scope = new ActivityScope();
        var logger = new CapturingLogger();
        OtelLogsBridge.BeginGenAiScope(logger).ShouldBeNull();
        logger.Scopes.ShouldBeEmpty();
    }

    [Fact]
    public void BeginGenAiScope_OpensScopeWithGenAiTagsFromActivity()
    {
        using var scope = new ActivityScope();
        scope.Activity.SetTag("gen_ai.system", "openai");
        scope.Activity.SetTag("gen_ai.request.model", "gpt-4");
        scope.Activity.SetTag("ignore.this", "no");
        var logger = new CapturingLogger();
        using var s = OtelLogsBridge.BeginGenAiScope(logger);
        s.ShouldNotBeNull();
        var dict = (IReadOnlyDictionary<string, object?>)logger.Scopes.Single();
        dict["gen_ai.system"].ShouldBe("openai");
        dict["gen_ai.request.model"].ShouldBe("gpt-4");
        dict.ShouldNotContainKey("ignore.this");
    }

    [Fact]
    public void BeginGenAiScope_ThrowsOnNullLogger()
    {
        Should.Throw<ArgumentNullException>(() => OtelLogsBridge.BeginGenAiScope(null!));
    }

    [Fact]
    public void Snapshot_ReturnsEmptyForNullActivity()
    {
        OtelLogsBridge.Snapshot(null).Count.ShouldBe(0);
    }

    [Fact]
    public void Snapshot_ReturnsEmptyForActivityWithoutGenAiTags()
    {
        using var scope = new ActivityScope();
        OtelLogsBridge.Snapshot(scope.Activity).Count.ShouldBe(0);
    }

    [Fact]
    public void Snapshot_ProjectsAllowlistedGenAiTags()
    {
        using var scope = new ActivityScope();
        scope.Activity.SetTag("gen_ai.system", "anthropic");
        scope.Activity.SetTag("gen_ai.usage.input_tokens", 42L);
        scope.Activity.SetTag("not.allowlisted", "x");
        var snap = OtelLogsBridge.Snapshot(scope.Activity);
        snap["gen_ai.system"].ShouldBe("anthropic");
        snap["gen_ai.usage.input_tokens"].ShouldBe(42L);
        snap.Count.ShouldBe(2);
    }

    [Fact]
    public void GenAiTagNames_IncludesExpectedAllowlist()
    {
        OtelLogsBridge.GenAiTagNames.ShouldContain("gen_ai.system");
        OtelLogsBridge.GenAiTagNames.ShouldContain("gen_ai.request.model");
        OtelLogsBridge.GenAiTagNames.ShouldContain("gen_ai.usage.input_tokens");
        OtelLogsBridge.GenAiTagNames.ShouldContain("gen_ai.operation.name");
    }

    [Fact]
    public void BeginGenAiScope_AcceptsNullLoggerInstance()
    {
        Should.NotThrow(() => OtelLogsBridge.BeginGenAiScope(NullLogger.Instance));
    }
}

#pragma warning restore AMPELUM_V09_PREVIEW
