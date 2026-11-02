using Ampelum.Samples.ResearchAssistant;
using Ampelum.Samples.Shared;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Budget;
using SignalGrid.Ampelum.Tools.WebSearch;

var fake = args.Contains("--fake");
var question = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? ResearchData.SampleQuestion;

IChatClient chat;
HttpClient http;
IWebSearchProvider search;
if (fake)
{
    chat = new FakeModel();
    http = new HttpClient(new FakeWebHandler());
    search = new BraveSearchProvider(http, "fake-search-key");
}
else
{
    var config = SampleConfig.Load();
    chat = config.CreateChatClient();
    http = new HttpClient();
    // Wikipedia rejects requests that carry no user agent.
    http.DefaultRequestHeaders.UserAgent.ParseAdd("AmpelumResearchSample/1.0");
    search = new BraveSearchProvider(http, config.Require("BRAVE_API_KEY"));
}

var options = new ResearchOptions();
using var knowledge = await KnowledgeBase.BuildAsync(
    ResearchData.KnowledgeDirectory, new HashingEmbeddingGenerator(), options);
using var assistant = new ResearchAssistantPipeline(
    chat, knowledge, http, search, Budget.Create().WithCallLimit(20).Build(), options);

var result = await assistant.AskAsync(question);

Console.WriteLine($"Question:  {result.Question}");
Console.WriteLine($"Agents:    {string.Join(" -> ", result.Speakers)} ({result.Termination})");
Console.WriteLine($"Queries:   {string.Join(" | ", result.Queries)}");
Console.WriteLine($"Passages:  {string.Join(", ", result.PassageIds)}");
foreach (var call in result.ToolCalls)
    Console.WriteLine($"Tool:      {call.Name}({call.Argument}) = {call.Output ?? "ERROR " + call.Error}");
Console.WriteLine($"Answer:    {result.Answer}");
Console.WriteLine($"Cited:     {string.Join(", ", result.Citations)}; unknown: {result.UnknownCitations.Count}");
Console.WriteLine($"Grounded:  {result.IsGrounded}");
return result.IsGrounded ? 0 : 1;
