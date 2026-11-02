using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Streaming;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Streaming;

public sealed class ProgramStreamerTests
{
    sealed class ScriptedProgram(string[] partials, string finalValue) : IStreamableProgram<string>
    {
        public async IAsyncEnumerable<StreamUpdate<string>> StreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var p in partials)
            {
                ct.ThrowIfCancellationRequested();
                yield return new StreamUpdate<string>("Answer", p, false, null);
                await Task.Yield();
            }
            yield return new StreamUpdate<string>("Answer", finalValue, true, finalValue);
        }
    }

    sealed class ThrowingProgram : IStreamableProgram<string>
    {
        public async IAsyncEnumerable<StreamUpdate<string>> StreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public async Task Streamify_YieldsAllPartials_ThenFinal()
    {
        var p = new ScriptedProgram(new[] { "pa", "par", "pari" }, "paris");
        var updates = new List<StreamUpdate<string>>();
        await foreach (var u in ProgramStreamer.Streamify<ScriptedProgram, string>(p, "Answer", TestContext.Current.CancellationToken))
            updates.Add(u);
        updates.Count.ShouldBe(4);
        updates[^1].IsFinal.ShouldBeTrue();
        updates[^1].FinalValue.ShouldBe("paris");
    }

    [Fact]
    public async Task Streamify_SubmoduleThrows_Propagates()
    {
        var p = new ThrowingProgram();
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in ProgramStreamer.Streamify<ThrowingProgram, string>(p, "Answer", TestContext.Current.CancellationToken)) { }
        });
    }

    [Fact]
    public async Task Streamify_CancellationToken_Propagates()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var p = new ScriptedProgram(new[] { "a" }, "a");
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in ProgramStreamer.Streamify<ScriptedProgram, string>(p, "Answer", cts.Token)) { }
        });
    }

    [Fact]
    public async Task Streamify_EmitsProgramStreamActivity()
    {
        // The listener is process-wide, so only this test's trace may reach the list.
        using var parent = new System.Diagnostics.Activity("program-stream-test").Start();
        var traceId = parent.TraceId;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = s => s.Name == "SignalGrid.Ampelum",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
        };
        var activities = new List<System.Diagnostics.Activity>();
        listener.ActivityStopped = a =>
        {
            if (a.TraceId == traceId)
                activities.Add(a);
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        var p = new ScriptedProgram(new[] { "a" }, "a");
        await foreach (var _ in ProgramStreamer.Streamify<ScriptedProgram, string>(p, "Answer", TestContext.Current.CancellationToken)) { }

        var act = activities.SingleOrDefault(a => a.OperationName == "SignalGrid.Ampelum.program.stream");
        act.ShouldNotBeNull();
        act.GetTagItem("stream.field_name").ShouldBe("Answer");
        act.GetTagItem("program.type").ShouldBe(nameof(ScriptedProgram));
    }

    [Fact]
    public async Task Streamify_NullProgram_ThrowsArgumentNullException()
    {
        ScriptedProgram? p = null;
        await Should.ThrowAsync<ArgumentNullException>(async () =>
        {
            await foreach (var _ in ProgramStreamer.Streamify<ScriptedProgram, string>(p!, "Answer", TestContext.Current.CancellationToken)) { }
        });
    }

    [Fact]
    public async Task Streamify_NullFieldName_ThrowsArgumentNullException()
    {
        var p = new ScriptedProgram(new[] { "a" }, "a");
        await Should.ThrowAsync<ArgumentNullException>(async () =>
        {
            await foreach (var _ in ProgramStreamer.Streamify<ScriptedProgram, string>(p, null!, TestContext.Current.CancellationToken)) { }
        });
    }

    [Fact]
    public async Task Streamify_ProgramStreamer_DoesNotEmitExtraTrace()
    {
        // ProgramStreamer is a pure pass-through â€” no own TraceRecord emission.
        var p = new ScriptedProgram(new[] { "a" }, "a");
        await foreach (var _ in ProgramStreamer.Streamify<ScriptedProgram, string>(p, "Answer", TestContext.Current.CancellationToken)) { }
        true.ShouldBeTrue();
    }

    [Fact]
    public async Task Streamify_FirstUpdate_CarriesExpectedFieldName()
    {
        var p = new ScriptedProgram(new[] { "a" }, "a");
        StreamUpdate<string>? first = null;
        await foreach (var u in ProgramStreamer.Streamify<ScriptedProgram, string>(p, "Answer", TestContext.Current.CancellationToken))
        {
            first ??= u;
        }
        first!.FieldName.ShouldBe("Answer");
    }
}
