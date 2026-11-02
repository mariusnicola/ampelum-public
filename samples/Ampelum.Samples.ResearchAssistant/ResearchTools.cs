using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Tools.Calculator;
using SignalGrid.Ampelum.Tools.CodeInterpreter;
using SignalGrid.Ampelum.Tools.WebSearch;
using SignalGrid.Ampelum.Tools.Wikipedia;

namespace Ampelum.Samples.ResearchAssistant;

/// <summary>
/// The analyst's tools. Each pre-built tool takes its argument in the constructor, so every call builds a
/// new tool object. Calls and the source ids they hand out are logged so the answer can be checked.
/// </summary>
public sealed class ResearchTools
{
    readonly HttpClient _http;
    readonly IWebSearchProvider _search;
    readonly int _searchTopK;
    int _webCounter;

    public ResearchTools(HttpClient http, IWebSearchProvider search, int searchTopK)
    {
        _http = http;
        _search = search;
        _searchTopK = searchTopK;
        Functions =
        [
            AIFunctionFactory.Create(Calculate, "calculator", "Evaluates an arithmetic expression such as 6 * 1100."),
            AIFunctionFactory.Create(Wikipedia, "wikipedia", "Returns the lead paragraph of an English Wikipedia article."),
            AIFunctionFactory.Create(WebSearch, "web_search", "Searches the web and returns hits with a source id each."),
            AIFunctionFactory.Create(RunCode, "code_interpreter", "Evaluates one short C# arithmetic expression using Math only."),
        ];
    }

    public IReadOnlyList<AIFunction> Functions { get; }

    public List<ToolCall> Calls { get; } = [];

    /// <summary>Source ids handed to the model by tools, for example wiki:Photovoltaics.</summary>
    public HashSet<string> SourceIds { get; } = new(StringComparer.Ordinal);

    async Task<string> Calculate([Description("Arithmetic expression, for example 6 * 1100.")] string expression, CancellationToken ct) =>
        await Record("calculator", expression, async () =>
        {
            var result = await new CalculatorTool(expression).InvokeAsync(ct);
            return result.Value.ToString(CultureInfo.InvariantCulture);
        });

    async Task<string> Wikipedia([Description("Article title, for example Photovoltaics.")] string title, CancellationToken ct) =>
        await Record("wikipedia", title, async () =>
        {
            var page = await new WikipediaTool(_http, title).InvokeAsync(ct);
            // the answer parser splits a bracket on commas, so a comma must not live inside an id
            var id = $"wiki:{page.Title.Replace(",", "")}";
            SourceIds.Add(id);
            return $"[{id}] {page.Extract} ({page.Url})";
        });

    async Task<string> WebSearch([Description("Search query.")] string query, CancellationToken ct) =>
        await Record("web_search", query, async () =>
        {
            var hits = await new WebSearchTool(_search, query, _searchTopK).InvokeAsync(ct);
            if (hits.Count == 0)
                return "No results.";
            var lines = new List<string>();
            foreach (var hit in hits)
            {
                var id = $"web:{++_webCounter}";
                SourceIds.Add(id);
                lines.Add($"[{id}] {hit.Title}: {hit.Snippet} ({hit.Url})");
            }
            return string.Join("\n", lines);
        });

    async Task<string> RunCode([Description("One C# expression, for example Math.Ceiling(18.1 / 10).")] string code, CancellationToken ct) =>
        await Record("code_interpreter", code, async () =>
        {
            CodeGuard.Check(code);
            return (await new CodeInterpreterTool(code).InvokeAsync(ct)).Display;
        });

    async Task<string> Record(string name, string argument, Func<Task<string>> run)
    {
        try
        {
            var output = await run();
            Calls.Add(new ToolCall(name, argument, output, null));
            return output;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Calls.Add(new ToolCall(name, argument, null, ex.Message));
            throw;
        }
    }
}

/// <summary>
/// The code interpreter runs in this process without a sandbox and without a timeout, so this guard is the
/// only limit: short expressions of arithmetic and Math calls pass. Everything else is refused, including
/// strings, other types, LINQ and statements.
/// </summary>
public static partial class CodeGuard
{
    // No LINQ and no statements: nothing here can loop or allocate in proportion to its input.
    const int MaxLength = 200;

    static readonly HashSet<string> AllowedNames = new(StringComparer.Ordinal)
    {
        "Math", "Ceiling", "Floor", "Round", "Sqrt", "Pow", "Abs", "Max", "Min",
        "double", "int", "long", "decimal",
    };

    [GeneratedRegex(@"^[A-Za-z0-9_\s\+\-\*/%\(\)\.,<>=!&\|\?:]+$")]
    private static partial Regex Characters();

    [GeneratedRegex("[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Names();

    public static void Check(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > MaxLength || !Characters().IsMatch(code))
            throw new InvalidOperationException("Code refused: only short arithmetic expressions are allowed.");
        foreach (Match m in Names().Matches(code))
        {
            var name = m.Value;
            var isVariable = name.Length == 1 && char.IsLower(name[0]);
            if (!isVariable && !AllowedNames.Contains(name))
                throw new InvalidOperationException($"Code refused: '{name}' is not allowed.");
        }
    }
}
