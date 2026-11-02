# Ampelum.Tools.WebSearch

Pre-built `ITypedTool<IReadOnlyList<SearchResult>>` for web search. Default backend: Brave Search.

## Usage

```csharp
using Ampelum.Tools.WebSearch;

var http = new HttpClient();
var provider = new BraveSearchProvider(http, Environment.GetEnvironmentVariable("BRAVE_API_KEY")!);
var tool = new WebSearchTool(provider, "What is DSPy?", topK: 3);
var results = await tool.InvokeAsync(ct);
```

## POLC tags

- `[ToolCategory(ToolCategoryKind.External)]`
- `network:egress` — issues outbound HTTPS calls.
- `data:public-internet` — returns content from arbitrary websites.

## Other providers

`WebSearchTool` accepts any `IWebSearchProvider` — implement Bing/SerpAPI similarly.

## Rate limits

Brave Search free tier: ~1 query/sec, 2000/month. Plug your own throttling middleware via `HttpClient` handlers.
