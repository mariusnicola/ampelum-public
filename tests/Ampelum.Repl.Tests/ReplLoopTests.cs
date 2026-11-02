using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Repl;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Repl.Tests;

public sealed class ReplLoopTests
{
    static ReplHost NewHost()
    {
        var ctx = new AmpelumContext(new FakeChatClient(), new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        return new ReplHost(new ReplGlobals(ctx));
    }

    static (StringWriter Out, StringWriter Err) Run(ReplHost host, string script)
    {
        var inp = new StringReader(script);
        var outp = new StringWriter();
        var err = new StringWriter();
        var loop = new ReplLoop(host, inp, outp, err);
        loop.RunAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        return (outp, err);
    }

    [Fact]
    public void Quit_ExitsCleanly()
    {
        var (o, e) = Run(NewHost(), ":quit\n");
        e.ToString().ShouldBeEmpty();
        o.ToString().ShouldContain("Ampelum REPL");
    }

    [Fact]
    public void Eof_ExitsCleanly()
    {
        var (o, _) = Run(NewHost(), "");
        o.ToString().ShouldContain("Ampelum REPL");
    }

    [Fact]
    public void Eval_PrintsResult()
    {
        var (o, _) = Run(NewHost(), "1 + 2\n:quit\n");
        o.ToString().ShouldContain("3");
    }

    [Fact]
    public void Eval_BlankLines_AreSkipped()
    {
        var (o, _) = Run(NewHost(), "\n\n:quit\n");
        o.ToString().ShouldContain("Ampelum REPL");
    }

    [Fact]
    public void Eval_VoidExpression_NoLine()
    {
        var (o, _) = Run(NewHost(), "var z = 9;\n:quit\n");
        o.ToString().ShouldNotContain("9\n");
    }

    [Fact]
    public void SaveAndLoad_Slots()
    {
        var host = NewHost();
        var (o, _) = Run(host, "var a = 11;\n:save s1\n:load s1\n:load missing\n:quit\n");
        var output = o.ToString();
        output.ShouldContain("saved s1");
        output.ShouldContain("loaded s1");
        output.ShouldContain("no such slot: missing");
    }

    [Fact]
    public void Eval_StringResult_PrintedRaw()
    {
        var (o, _) = Run(NewHost(), "\"hello\"\n:quit\n");
        o.ToString().ShouldContain("hello");
    }

    [Fact]
    public void Eval_FormattableResult_UsesInvariant()
    {
        var (o, _) = Run(NewHost(), "1.5\n:quit\n");
        o.ToString().ShouldContain("1.5");
    }

    [Fact]
    public void Eval_SyntaxError_WritesToError()
    {
        var (_, e) = Run(NewHost(), "@@bogus@@\n:quit\n");
        e.ToString().ShouldContain("error:");
    }

    [Fact]
    public async Task Cancellation_StopsLoop()
    {
        var host = NewHost();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var inp = new StringReader("1\n:quit\n");
        var outp = new StringWriter();
        var loop = new ReplLoop(host, inp, outp, new StringWriter());
        // Pre-cancelled: loop emits banner then exits the while-loop without reading input.
        await loop.RunAsync(cts.Token);
        outp.ToString().ShouldContain("Ampelum REPL");
    }
}
