using Ampelum.Samples.ResearchAssistant;
using Microsoft.Extensions.AI;
using Xunit;
using SignalGrid.Ampelum.Budget;
using SignalGrid.Ampelum.Retrieval.Hybrid;
using SignalGrid.Ampelum.Tools.WebSearch;

namespace Ampelum.Samples.ResearchAssistant.Tests;

/// <summary>One assembled assistant with its fake model and fake web, so a test can read what each one received.</summary>
sealed class Rig : IDisposable
{
    Rig(ResearchAssistantPipeline pipeline, KnowledgeBase knowledge, FakeModel model, FakeWebHandler web, HttpClient http, IWebSearchProvider search)
    {
        Pipeline = pipeline;
        Knowledge = knowledge;
        Model = model;
        Web = web;
        Http = http;
        Search = search;
    }

    public ResearchAssistantPipeline Pipeline { get; }
    public KnowledgeBase Knowledge { get; }
    public FakeModel Model { get; }
    public FakeWebHandler Web { get; }
    public HttpClient Http { get; }
    public IWebSearchProvider Search { get; }

    public static async Task<Rig> BuildAsync(
        Func<FakeModel, IChatClient>? wrap = null,
        string? directory = null,
        int callLimit = 30,
        ResearchOptions? options = null,
        IReranker? reranker = null)
    {
        options ??= new ResearchOptions();
        var model = new FakeModel();
        var web = new FakeWebHandler();
        var http = new HttpClient(web);
        IWebSearchProvider search = new BraveSearchProvider(http, "test-token");
        var knowledge = await KnowledgeBase.BuildAsync(
            directory ?? ResearchData.KnowledgeDirectory,
            new HashingEmbeddingGenerator(),
            options,
            reranker,
            TestContext.Current.CancellationToken);
        var pipeline = new ResearchAssistantPipeline(
            wrap?.Invoke(model) ?? model, knowledge, http, search, Budget.Create().WithCallLimit(callLimit).Build(), options);
        return new Rig(pipeline, knowledge, model, web, http, search);
    }

    public string PromptOfCall(int index) =>
        string.Join("\n", Model.Inner.RecordedCalls[index].Messages.SelectMany(m => m.Contents).Select(c => c switch
        {
            TextContent t => t.Text,
            FunctionResultContent r => r.Result?.ToString() ?? "",
            FunctionCallContent f => $"{f.Name}({string.Join(",", f.Arguments?.Values ?? [])})",
            _ => "",
        }));

    public void Dispose()
    {
        Pipeline.Dispose();
        Knowledge.Dispose();
        Http.Dispose();
    }
}
