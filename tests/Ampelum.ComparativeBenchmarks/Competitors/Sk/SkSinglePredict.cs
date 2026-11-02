using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Ampelum.ComparativeBenchmarks.Competitors.Sk;

/// <summary>
/// Real Semantic-Kernel single-Predict harness — replaces the alpha.4 placeholder.
/// <para>
/// Wires the supplied MEAI <see cref="IChatClient"/> into a real <see cref="Kernel"/>
/// via <see cref="ChatClientExtensions.AsChatCompletionService(IChatClient, IServiceProvider)"/>,
/// registers the resulting <see cref="IChatCompletionService"/> on the kernel
/// builder's service collection, then invokes the prompt through SK's idiomatic
/// <see cref="KernelExtensions.InvokePromptAsync(Kernel, string, KernelArguments?, string?, IPromptTemplateFactory?, PromptTemplateConfig?, CancellationToken)"/>
/// path. The chat call cost is byte-identical to <c>MeaiRaw</c>; the delta is
/// SK Kernel + KernelFunction template-render + dispatch overhead.
/// </para>
/// </summary>
public static class SkSinglePredict
{
    /// <summary>Build a kernel that routes prompts through the supplied <paramref name="chat"/> client.</summary>
    public static Kernel BuildKernel(IChatClient chat)
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(sp => chat.AsChatCompletionService(sp));
        return builder.Build();
    }

    public static async Task<string> RunAsync(Kernel kernel, string question)
    {
        // Real SK template render + KernelFunction dispatch + filter pipeline.
        var result = await kernel.InvokePromptAsync(question);
        return result.GetValue<string>() ?? string.Empty;
    }
}
