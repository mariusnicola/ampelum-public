using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.AttributePolicy;


[Collection(ActivityListenerCollection.Name)]
public sealed class AttributeAllowlistTests : IDisposable
{
    public AttributeAllowlistTests()
    {
        SignalGrid.Ampelum.Diagnostics.StrictAttributePolicy = false;
        SignalGrid.Ampelum.Diagnostics.AttributePolicyLogger = null;
    }

    public void Dispose()
    {
        SignalGrid.Ampelum.Diagnostics.StrictAttributePolicy = false;
        SignalGrid.Ampelum.Diagnostics.AttributePolicyLogger = null;
    }

    [Fact]
    public void SetTag_AllowlistedKey_DoesNotWarn()
    {
        var logger = new RecordingLogger();
        SignalGrid.Ampelum.Diagnostics.AttributePolicyLogger = logger;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "SignalGrid.Ampelum",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = SignalGrid.Ampelum.Diagnostics.Source.StartActivity("test");
        SignalGrid.Ampelum.Diagnostics.SetTag(activity, "SignalGrid.Ampelum.module", "Predict");
        SignalGrid.Ampelum.Diagnostics.SetTag(activity, "gen_ai.usage.input_tokens", 10);
        SignalGrid.Ampelum.Diagnostics.SetTag(activity, "exception.type", "Foo");
        SignalGrid.Ampelum.Diagnostics.SetTag(activity, "tool.name", "calculator");
        logger.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void SetTag_DisallowedKey_LogsWarning()
    {
        var logger = new RecordingLogger();
        SignalGrid.Ampelum.Diagnostics.AttributePolicyLogger = logger;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "SignalGrid.Ampelum",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = SignalGrid.Ampelum.Diagnostics.Source.StartActivity("test");
        SignalGrid.Ampelum.Diagnostics.SetTag(activity, "user.prompt.full_text", "hello world");
        logger.Warnings.ShouldHaveSingleItem();
        logger.Warnings[0].ShouldContain("user.prompt.full_text");
    }

    [Fact]
    public void SetTag_StrictPolicyOn_DisallowedKeyThrows()
    {
        SignalGrid.Ampelum.Diagnostics.StrictAttributePolicy = true;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "SignalGrid.Ampelum",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = SignalGrid.Ampelum.Diagnostics.Source.StartActivity("test");
        Should.Throw<InvalidOperationException>(() =>
            SignalGrid.Ampelum.Diagnostics.SetTag(activity, "user.private.field", "x"));
    }

    [Fact]
    public void SetTag_NullActivity_StillValidatesKey()
    {
        var logger = new RecordingLogger();
        SignalGrid.Ampelum.Diagnostics.AttributePolicyLogger = logger;
        SignalGrid.Ampelum.Diagnostics.SetTag(null, "user.unknown", "value");
        logger.Warnings.ShouldHaveSingleItem();
    }

    [Fact]
    public void SetTag_NullKey_Throws() =>
        Should.Throw<ArgumentException>(() => SignalGrid.Ampelum.Diagnostics.SetTag(null, null!, "x"));

    [Fact]
    public void SetTag_EmptyKey_Throws() =>
        Should.Throw<ArgumentException>(() => SignalGrid.Ampelum.Diagnostics.SetTag(null, "", "x"));

    [Fact]
    public void SetTag_NoLogger_DisallowedKeyDoesNotThrowInWarnMode()
    {
        SignalGrid.Ampelum.Diagnostics.AttributePolicyLogger = null;
        SignalGrid.Ampelum.Diagnostics.SetTag(null, "user.unknown", "value");
        // No-op â€” warn mode without a configured logger silently emits.
    }

    sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
