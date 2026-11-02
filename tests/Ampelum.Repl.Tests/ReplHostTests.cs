using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Repl;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Repl.Tests;

public sealed class ReplHostTests
{
    static ReplHost NewHost(out FakeChatClient fake)
    {
        fake = new FakeChatClient();
        var ctx = new AmpelumContext(fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        return new ReplHost(new ReplGlobals(ctx));
    }

    [Fact]
    public async Task EvaluateAsync_RunsArithmetic()
    {
        var host = NewHost(out _);
        var v = await host.EvaluateAsync("2 + 3", TestContext.Current.CancellationToken);
        v.ShouldBe(5);
    }

    [Fact]
    public async Task EvaluateAsync_PersistsVariablesAcrossCalls()
    {
        var host = NewHost(out _);
        await host.EvaluateAsync("var x = 41;", TestContext.Current.CancellationToken);
        var r = await host.EvaluateAsync("x + 1", TestContext.Current.CancellationToken);
        r.ShouldBe(42);
    }

    [Fact]
    public async Task EvaluateAsync_GlobalsExposeAmpelumContext()
    {
        var host = NewHost(out _);
        var r = await host.EvaluateAsync("Ctx.Chat.GetType().Name", TestContext.Current.CancellationToken);
        r.ShouldBe("FakeChatClient");
    }

    [Fact]
    public async Task SaveLoad_RoundTrips_Variables()
    {
        var host = NewHost(out _);
        await host.EvaluateAsync("var foo = 7;", TestContext.Current.CancellationToken);
        host.Save("snap1");
        var snap = host.Load("snap1");
        snap.ShouldNotBeNull();
        snap!["foo"].ShouldBe(7);
    }

    [Fact]
    public void Load_UnknownSlot_ReturnsNull()
    {
        var host = NewHost(out _);
        host.Load("missing").ShouldBeNull();
    }

    [Fact]
    public void Save_BeforeAnyEvaluation_SavesEmpty()
    {
        var host = NewHost(out _);
        host.Save("empty");
        host.Load("empty").ShouldBeNull(); // null sentinel — no state yet
    }

    [Fact]
    public void CurrentVariables_BeforeEvaluation_IsEmpty()
    {
        var host = NewHost(out _);
        host.CurrentVariables().Count.ShouldBe(0);
    }

    [Fact]
    public async Task CurrentVariables_AfterEvaluation_ContainsDeclared()
    {
        var host = NewHost(out _);
        await host.EvaluateAsync("var alpha = \"hello\";", TestContext.Current.CancellationToken);
        var vars = host.CurrentVariables();
        vars["alpha"].ShouldBe("hello");
    }
}
