using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Testing;

namespace Ampelum.Samples.ResearchAssistant;

/// <summary>
/// Stand-in model for fake mode and tests. It answers each call from the messages it was sent, routed by the
/// signature instructions, so a missing passage or tool result changes what it says. Every call is recorded by
/// <see cref="Inner"/>.
/// </summary>
public sealed partial class FakeModel : IChatClient
{
    public FakeChatClient Inner { get; } = new();

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sent = messages.ToArray();
        Inner.EnqueueRaw(Respond(sent));
        return Inner.GetResponseAsync(sent, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The research assistant does not stream.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => Inner.Dispose();

    static ChatResponse Respond(IReadOnlyList<ChatMessage> messages)
    {
        var system = messages.First(m => m.Role == ChatRole.System).Text;
        if (system.Contains(Prompts.Plan, StringComparison.Ordinal))
            return Text(JsonSerializer.Serialize(new { queries = Plan(JsonInput(messages, "question")) }));
        if (system.Contains(Prompts.Analyse, StringComparison.Ordinal))
            return Analyse(messages);
        if (system.Contains(Prompts.Write, StringComparison.Ordinal))
            return Text(JsonSerializer.Serialize(new
            {
                answer = Write(JsonInput(messages, "findings"), JsonInput(messages, "passages")),
            }));
        throw new InvalidOperationException("FakeModel: no script for this prompt.");
    }

    static string Plan(string question)
    {
        var queries = new List<string>();
        if (question.Contains("solar", StringComparison.OrdinalIgnoreCase))
            queries.Add("rooftop solar yield kWh per kW per year");
        if (question.Contains("battery", StringComparison.OrdinalIgnoreCase))
            queries.Add("home battery module usable kWh");
        if (question.Contains("wind", StringComparison.OrdinalIgnoreCase))
            queries.Add("small wind turbine yield per year");
        return queries.Count == 0 ? question : string.Join("; ", queries);
    }

    /// <summary>One policy for every analyst turn: it looks at which tools were already called and at their results.</summary>
    static ChatResponse Analyse(IReadOnlyList<ChatMessage> messages)
    {
        var input = messages.First(m => m.Role == ChatRole.User).Text;
        var split = input.IndexOf("Passages:", StringComparison.Ordinal);
        var question = input[..split];
        var passages = input[split..];

        var yield = Number(YieldPattern(), passages);
        var kw = Number(SizePattern(), question);
        var module = Number(ModulePattern(), passages);
        var calls = messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        string? ResultOf(string tool, int nth = 0) => calls.Where(c => c.Name == tool).Skip(nth).FirstOrDefault() is { } call
            ? messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
                .FirstOrDefault(r => r.CallId == call.CallId)?.Result?.ToString()
            : null;
        int Count(string tool) => calls.Count(c => c.Name == tool);

        if (Count("calculator") == 0)
        {
            if (yield is null || kw is null)
                return Final("The passages do not give a yield per kW or the question gives no array size.");
            return Call("calculator", "expression", $"{Format(kw.Value)} * {Format(yield.Value)}");
        }
        var annual = ResultOf("calculator");
        if (Count("calculator") == 1)
            return Call("calculator", "expression", $"{annual} / 365");
        if (Count("wikipedia") == 0 && passages.Contains("photovoltaic", StringComparison.OrdinalIgnoreCase))
            return Call("wikipedia", "title", "Photovoltaics");
        if (Count("web_search") == 0)
        {
            var region = question.Contains("Central Europe", StringComparison.Ordinal) ? " Central Europe" : "";
            return Call("web_search", "query", $"solar yield{region}");
        }
        if (Count("code_interpreter") == 0 && module is not null)
            return Call("code_interpreter", "code", $"Math.Ceiling((double){annual} / 365 / {Format(module.Value)})");

        var daily = ResultOf("calculator", 1);
        var modules = ResultOf("code_interpreter");
        var wiki = ResultOf("wikipedia");
        var web = ResultOf("web_search");
        var text = $"Annual output: {Round(annual)} kWh ({Format(kw!.Value)} kW x {Format(yield!.Value)} kWh per kW) [{IdOf(passages, "kWh per kW")}]. "
                   + $"Daily output: {Round(daily)} kWh. "
                   + (modules is null || modules.StartsWith("Code refused", StringComparison.Ordinal)
                       ? "Battery modules needed: unknown. "
                       : $"Battery modules needed: {modules} ({Format(module!.Value)} kWh usable each) [{IdOf(passages, "kWh usable")}]. ")
                   + (wiki is null || !wiki.StartsWith('[') ? "" : $"Background {FirstSource(wiki)}. ")
                   + (web is null || !web.StartsWith('[') ? "" : $"Web check {FirstSource(web)}.");
        return Final(text);
    }

    static string Write(string findings, string passages)
    {
        var annual = Regex.Match(findings, @"Annual output: ([\d.]+) kWh");
        if (!annual.Success)
            return "The sources do not contain enough information to answer.";
        var daily = Regex.Match(findings, @"Daily output: ([\d.]+) kWh").Groups[1].Value;
        var modules = Regex.Match(findings, @"Battery modules needed: (\d+)");
        var answer = $"The array makes about {annual.Groups[1].Value} kWh per year [{IdOf(passages, "kWh per kW")}], which is {daily} kWh a day";
        answer += modules.Success
            ? $". Storing one such day takes {modules.Groups[1].Value} battery modules [{IdOf(passages, "kWh usable")}]."
            : ", but the battery size could not be worked out.";
        foreach (Match source in Regex.Matches(findings, @"(Background|Web check) \[((?:wiki|web):[^\]]+)\]"))
            answer += $" {source.Groups[1].Value} [{source.Groups[2].Value}].";
        return answer;
    }

    static string FirstSource(string toolOutput)
    {
        var m = Regex.Match(toolOutput, @"^(\[[^\]]+\])");
        return m.Success ? m.Value : "[unknown]";
    }

    static string IdOf(string passages, string phrase)
    {
        var m = Regex.Match(passages, @"\[([^\]\s]+)\][^\n]*" + Regex.Escape(phrase));
        return m.Success ? m.Groups[1].Value : "missing";
    }

    static string JsonInput(IReadOnlyList<ChatMessage> messages, string name)
    {
        using var doc = JsonDocument.Parse(messages.Last(m => m.Role == ChatRole.User).Text);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.GetString() ?? "";
        }
        throw new InvalidOperationException($"FakeModel: input '{name}' was not sent.");
    }

    static double? Number(Regex pattern, string text) =>
        pattern.Match(text) is { Success: true } m
            ? double.Parse(m.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture)
            : null;

    static string Round(string? value) =>
        double.Parse(value ?? "0", CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.InvariantCulture);

    static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);

    static ChatResponse Call(string tool, string argument, string value)
    {
        var turn = new ChatMessage(ChatRole.Assistant, "");
        turn.Contents.Add(new FunctionCallContent(
            $"call-{Guid.NewGuid():N}", tool, new Dictionary<string, object?> { [argument] = value }));
        return new ChatResponse(turn);
    }

    static ChatResponse Final(string text) => Text($"Thought: I have what I need.\nfinal_answer: {text}");

    static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    [GeneratedRegex(@"([\d,]+(?:\.\d+)?) kWh per kW")]
    private static partial Regex YieldPattern();

    [GeneratedRegex(@"(\d+(?:\.\d+)?) kW\b")]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"(\d+(?:\.\d+)?) kWh usable")]
    private static partial Regex ModulePattern();
}
