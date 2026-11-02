using Ampelum.ComparativeBenchmarks.Competitors.AutoGenNet;
using Ampelum.ComparativeBenchmarks.Competitors.LangChainNet;
using Ampelum.ComparativeBenchmarks.Competitors.Shared;
using Ampelum.ComparativeBenchmarks.Competitors.Sk;
using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--smoke")
{
    var mock = new MockChatClient("[[ ## Answer ## ]]\n42\n[[ ## completed ## ]]");
    var sk = SkSinglePredict.BuildKernel(mock);
    Console.WriteLine("SK: " + await SkSinglePredict.RunAsync(sk, "q"));
    var skp = SkPipeline.BuildKernel(mock);
    Console.WriteLine("SK4: " + await SkPipeline.RunAsync(skp, "q"));
    var chunked = new ChunkedMockChatClient("[[ ## Answer ## ]]\n42\n[[ ## completed ## ]]", 100);
    var sks = SkStreaming.BuildKernel(chunked);
    Console.WriteLine("SK-stream: " + await SkStreaming.RunAsync(sks, "q"));
    var ag = AutoGenSinglePredict.BuildAgent(mock);
    Console.WriteLine("AG: " + await AutoGenSinglePredict.RunAsync(ag, "q"));
    var lc = LangChainSinglePredict.BuildChain(mock);
    Console.WriteLine("LC: " + await LangChainSinglePredict.RunAsync(lc, "q"));
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
