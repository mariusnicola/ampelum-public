using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum.Agents.Memory;
using SignalGrid.Ampelum.Agents.SpeakerSelectors;
using SignalGrid.Ampelum.Budget;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Tools.WebSearch;

namespace Ampelum.Samples.ResearchAssistant;

/// <summary>
/// A question goes through four specialist agents in a group chat: planner, researcher, analyst and writer.
/// The answer must cite sources, and every cited id has to be one a passage or a tool actually handed out.
/// </summary>
public sealed partial class ResearchAssistantPipeline : IDisposable
{
    readonly KnowledgeBase _knowledge;
    readonly HttpClient _http;
    readonly IWebSearchProvider _search;
    readonly ResearchOptions _options;
    readonly ServiceProvider _provider;

    public ResearchAssistantPipeline(
        IChatClient chat,
        KnowledgeBase knowledge,
        HttpClient http,
        IWebSearchProvider search,
        IBudget budget,
        ResearchOptions? options = null)
    {
        _knowledge = knowledge;
        _http = http;
        _search = search;
        _options = options ?? new ResearchOptions();

        var services = new ServiceCollection();
        services.AddSingleton(chat);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddAmpelum().UseAdapter<JsonAdapter>();
        services.AddSingleton<IHook>(new BudgetHook(budget));
        _provider = services.BuildServiceProvider();
    }

    public async Task<ResearchResult> AskAsync(string question, CancellationToken ct = default)
    {
        using var scope = _provider.CreateScope();
        var tools = new ResearchTools(_http, _search, _options.SearchTopK);
        var memory = new InMemoryConversationMemory(_options.MemoryCapacity);
        IAgent[] agents =
        [
            new PlannerAgent(scope.ServiceProvider),
            new ResearcherAgent(_knowledge, _options.PassagesPerQuery),
            new AnalystAgent(scope.ServiceProvider, tools, _options.MaxToolIterations),
            new WriterAgent(scope.ServiceProvider),
        ];
        var chat = new GroupChat(agents, new RoleBasedSelector(Roles.Order), memory);

        var run = await chat.RunAsync(question, _options.MaxTurns, ct);
        if (run.Reason != TerminationReason.TerminalMessage)
            throw new InvalidOperationException($"The conversation ended without an answer ({run.Reason}).");

        var answer = run.History[^1].Content;
        var queries = PlannerAgent.SplitQueries(run.History.Last(m => m.SenderName == Roles.Planner).Content);
        var researcher = run.History.Last(m => m.SenderName == Roles.Researcher);
        var passageIds = researcher.Metadata[ResearcherAgent.PassageIdsKey]
            .Split(',', StringSplitOptions.RemoveEmptyEntries);

        var allowed = passageIds.Concat(tools.SourceIds).ToHashSet(StringComparer.Ordinal);
        // one bracket may hold several ids; whitespace around an id must not hide it
        var cited = CitationPattern().Matches(answer)
            .SelectMany(m => m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct().ToList();
        return new ResearchResult(
            question,
            answer,
            run.History.Skip(1).Select(m => m.SenderName).ToList(),
            run.Reason,
            queries,
            passageIds,
            tools.Calls,
            cited.Where(allowed.Contains).ToList(),
            cited.Where(id => !allowed.Contains(id)).ToList());
    }

    [GeneratedRegex(@"\[([^\[\]]+)\]")]
    private static partial Regex CitationPattern();

    public void Dispose() => _provider.Dispose();
}
