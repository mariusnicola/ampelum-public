using BenchmarkDotNet.Attributes;
using Microsoft.SemanticKernel;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

/// <summary>
/// User-brief S5 — tool-calling round-trip.
///
/// Plan name: Scenario05_ToolCallingRoundTrip.
/// Budget B3: ≤5% behind SK on tool dispatch.
///
/// <para>Scope reduction (per advisor):</para>
/// A full LM-driven tool round-trip (prompt → tool_call → exec → final answer)
/// requires a mock that switches its canned response on the second call AND
/// SK's <c>AutoInvokeKernelFunctions</c> path which presumes a real
/// chat-completion service emitting <c>FunctionCallContent</c>. Wiring that
/// against an arbitrary <see cref="Microsoft.Extensions.AI.IChatClient"/> mock
/// is brittle and the resulting numbers are dominated by mock plumbing rather
/// than framework dispatch.
///
/// alpha.5 ships the dispatch-isolation cut: bench the per-tool **dispatch
/// + invoke** cost on the hot path of a tool round-trip, holding the LM
/// halves identical (zero) on both sides. The numbers therefore measure the
/// cost the framework adds to each tool call once the LM has already decided
/// to invoke it. alpha.6 adds the full two-call mock for the end-to-end path.
///
/// Three rows:
/// <list type="bullet">
///   <item><b>Ampelum</b> — invoke an <see cref="IReActTool"/> equivalent
///   adapter (string-args JSON in, string-result out) backed by a tiny
///   adder delegate.</item>
///   <item><b>Sk</b> — invoke an SK <see cref="KernelFunction"/> built via
///   <see cref="KernelFunctionFactory.CreateFromMethod(Delegate, string?, string?, System.Collections.Generic.IEnumerable{KernelParameterMetadata}?, KernelReturnParameterMetadata?, ILoggerFactory?)"/>
///   over the same delegate.</item>
///   <item><b>RawDelegate</b> — the underlying <c>Func&lt;int,int,int&gt;</c>
///   directly. Hard floor.</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
public class Scenario05_ToolCallingRoundTrip
{
    private static readonly Func<int, int, int> _add = (a, b) => a + b;

    private AmpelumAddTool _ampelumTool = null!;
    private KernelFunction _skFunction = null!;
    private KernelArguments _skArgs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _ampelumTool = new AmpelumAddTool();
        _skFunction = KernelFunctionFactory.CreateFromMethod(_add, "add");
        _skArgs = new KernelArguments { ["a"] = 2, ["b"] = 3 };
    }

    [Benchmark(Baseline = true)]
    public async ValueTask<string> Ampelum()
        => await _ampelumTool.InvokeAsync("{\"a\":2,\"b\":3}", default);

    [Benchmark]
    public async ValueTask<int> Sk()
    {
        var r = await _skFunction.InvokeAsync(new Kernel(), _skArgs);
        return r.GetValue<int>();
    }

    [Benchmark]
    public int RawDelegate() => _add(2, 3);

    /// <summary>
    /// Minimal Ampelum tool: parses two ints from a JSON args blob and returns
    /// the sum as a string. Mirrors the shape <c>ReAct&lt;TSig&gt;</c> hands
    /// to its <see cref="IReActTool"/> contract (string-in / string-out so
    /// the chat round can render the result without per-tool generic type).
    /// </summary>
    private sealed class AmpelumAddTool
    {
        public string Name => "add";

        public ValueTask<string> InvokeAsync(string argumentsJson, CancellationToken ct)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
            var a = doc.RootElement.GetProperty("a").GetInt32();
            var b = doc.RootElement.GetProperty("b").GetInt32();
            return ValueTask.FromResult((a + b).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
