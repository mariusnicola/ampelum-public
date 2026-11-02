# Ampelum.Tools.Wikipedia

Pre-built `ITypedTool<WikiSummary>` calling the Wikipedia REST summary API.

## Usage

```csharp
using Ampelum.Tools.Wikipedia;
using var http = new HttpClient();
var tool = new WikipediaTool(http, "Albert Einstein");
var s = await tool.InvokeAsync(ct);
Console.WriteLine(s.Extract);
```

## POLC tags

- `[ToolCategory(ToolCategoryKind.External)]`
- `network:egress`
- `data:public-internet`

No API key required.
