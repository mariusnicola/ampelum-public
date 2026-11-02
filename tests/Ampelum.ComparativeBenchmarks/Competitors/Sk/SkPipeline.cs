using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Ampelum.ComparativeBenchmarks.Competitors.Sk;

/// <summary>
/// Real Semantic-Kernel 4-filter pipeline — replaces the alpha.4 placeholder
/// (the <c>int s1Calls = 0; s1Calls++;</c> hand-rolled noop chain that the
/// 2026-04-26 audit flagged as "exhibit A" of fake competitor wiring).
/// <para>
/// Wires four real <see cref="IFunctionInvocationFilter"/> instances onto a
/// kernel that bridges the same MEAI <see cref="IChatClient"/> mock used by
/// the Ampelum row. Each filter wraps the function invocation with a
/// before / <c>await next</c> / after sandwich — matching the shape of
/// Ampelum's <c>OnBeforeAsync</c> / forward / <c>OnAfterAsync</c> hook
/// pipeline. The orchestration delta is now a real Kernel-vs-AmpelumContext
/// number rather than a no-op delegate increment.
/// </para>
/// </summary>
public static class SkPipeline
{
    /// <summary>Build a kernel with four noop function-invocation filters wired.</summary>
    public static Kernel BuildKernel(IChatClient chat)
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(sp => chat.AsChatCompletionService(sp));
        var kernel = builder.Build();
        kernel.FunctionInvocationFilters.Add(new NoopFilter());
        kernel.FunctionInvocationFilters.Add(new NoopFilter());
        kernel.FunctionInvocationFilters.Add(new NoopFilter());
        kernel.FunctionInvocationFilters.Add(new NoopFilter());
        return kernel;
    }

    public static async Task<string> RunAsync(Kernel kernel, string question)
    {
        var result = await kernel.InvokePromptAsync(question);
        return result.GetValue<string>() ?? string.Empty;
    }

    /// <summary>Cheapest possible filter — delegates straight through.
    /// Mirrors the <c>NoopHook</c> shape used by the Ampelum row in
    /// <c>Scenario02_PredictPlus4HookPipeline</c>.</summary>
    private sealed class NoopFilter : IFunctionInvocationFilter
    {
        public Task OnFunctionInvocationAsync(
            Microsoft.SemanticKernel.FunctionInvocationContext context,
            Func<Microsoft.SemanticKernel.FunctionInvocationContext, Task> next)
            => next(context);
    }
}
