using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Ampelum.ComparativeBenchmarks.Competitors.Sk;

/// <summary>
/// Real Semantic-Kernel streaming harness — replaces the alpha.4 placeholder
/// that was byte-identical to the <c>MeaiRaw</c> row.
/// <para>
/// Routes through SK's idiomatic streaming API
/// (<see cref="KernelExtensions.InvokePromptStreamingAsync(Kernel, string, KernelArguments?, string?, IPromptTemplateFactory?, PromptTemplateConfig?, CancellationToken)"/>)
/// against the same chunked mock the Ampelum row consumes. Per-chunk parse
/// cost on the SK side is the <see cref="StreamingKernelContent"/>
/// adapter overhead.
/// </para>
/// </summary>
public static class SkStreaming
{
    public static Kernel BuildKernel(IChatClient chat)
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(sp => chat.AsChatCompletionService(sp));
        return builder.Build();
    }

    public static async Task<int> RunAsync(Kernel kernel, string question)
    {
        int n = 0;
        await foreach (var _ in kernel.InvokePromptStreamingAsync(question))
            n++;
        return n;
    }
}
