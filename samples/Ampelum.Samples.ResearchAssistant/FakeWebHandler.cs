using System.Net;
using System.Text;
using System.Text.Json;

namespace Ampelum.Samples.ResearchAssistant;

public sealed record FakeWebRequest(string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Plays Wikipedia and Brave Search for fake mode and tests, so the real tool and provider code runs
/// against canned JSON. Answers depend on the request: the page title, the query terms and the token header.
/// </summary>
public sealed class FakeWebHandler : HttpMessageHandler
{
    public const string WikipediaHost = "en.wikipedia.org";
    public const string SearchHost = "api.search.brave.com";
    public const string TokenHeader = "X-Subscription-Token";

    static readonly Dictionary<string, (string Title, string Extract)> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Paris, Texas"] = ("Paris, Texas", "Paris is a city in Lamar County, Texas."),
        ["Solar panel"] = ("Solar panel",
            "A solar panel is a set of photovoltaic cells in a frame that turns sunlight into electricity."),
        ["Photovoltaics"] = ("Photovoltaics",
            "Photovoltaics is the conversion of light into electricity using semiconducting materials that show the photovoltaic effect."),
    };

    static readonly (string Title, string Url, string Description)[] Corpus =
    [
        ("Solar yield by region", "https://example.org/solar-yield", "Rooftop solar yield in Central Europe is typically 900 to 1,200 kWh per kW peak per year."),
        ("Home battery sizing guide", "https://example.org/battery-sizing", "Home battery modules come in 5 kWh to 15 kWh sizes; size to the evening demand."),
        ("Small wind turbines", "https://example.org/wind", "Small wind turbines suit open sites with steady wind."),
    ];

    readonly List<FakeWebRequest> _requests = [];

    public IReadOnlyList<FakeWebRequest> Requests => _requests;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        _requests.Add(new FakeWebRequest(request.Method.Method, uri, headers));

        if (uri.Host == WikipediaHost && uri.AbsolutePath.StartsWith("/api/rest_v1/page/summary/", StringComparison.Ordinal))
            return Task.FromResult(Wikipedia(Uri.UnescapeDataString(uri.AbsolutePath["/api/rest_v1/page/summary/".Length..])));
        if (uri.Host == SearchHost && uri.AbsolutePath == "/res/v1/web/search")
            return Task.FromResult(Search(uri, headers));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    static HttpResponseMessage Wikipedia(string title)
    {
        if (!Pages.TryGetValue(title.Replace('_', ' '), out var page))
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        return Json(new
        {
            title = page.Title,
            extract = page.Extract,
            content_urls = new { desktop = new { page = $"https://en.wikipedia.org/wiki/{page.Title}" } },
        });
    }

    static HttpResponseMessage Search(Uri uri, IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue(TokenHeader, out var token) || string.IsNullOrWhiteSpace(token))
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var query = ReadQuery(uri, "q");
        var count = int.TryParse(ReadQuery(uri, "count"), out var c) ? c : 3;
        var terms = TermText.Terms(query).ToHashSet(StringComparer.Ordinal);
        var results = Corpus
            .Select(h => (Hit: h, Score: TermText.Terms(h.Title + " " + h.Description).Count(terms.Contains)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(count)
            .Select(x => new { title = x.Hit.Title, url = x.Hit.Url, description = x.Hit.Description })
            .ToArray();
        return Json(new { web = new { results } });
    }

    static string ReadQuery(Uri uri, string key)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts[0] == key)
                return Uri.UnescapeDataString(parts.Length > 1 ? parts[1].Replace('+', ' ') : "");
        }
        return "";
    }

    static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
