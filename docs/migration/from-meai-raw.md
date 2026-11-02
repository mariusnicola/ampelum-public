# Migrating from raw `Microsoft.Extensions.AI` (`IChatClient`)

If you're already calling `IChatClient.GetResponseAsync` directly,
Ampelum sits one layer above: it adds typed signatures, automatic
prompt formatting, structured response parsing, optional CoT/ReAct,
hooks (audit/budget/retry), and an optimizer. The chat client itself
is unchanged.

## Single call

**Raw MEAI:**

```csharp
var prompt = $"Question: {question}\nAnswer:";
var response = await chat.GetResponseAsync(prompt, ct: ct);
string answer = response.Text;  // free-form, you parse
```

**Ampelum:**

```csharp
[Signature("Answer the question.")]
public sealed record Qa : ISignature
{
    [Input]  public required string Question { get; init; }
    [Output] public string Answer { get; init; } = "";
}

var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
var p = new Predict<Qa>(ctx);
var r = await p.ForwardAsync(new Qa { Question = question });
string answer = r.Value.Answer;  // typed, parsed
```

## Streaming

**Raw MEAI:**

```csharp
await foreach (var update in chat.GetStreamingResponseAsync(prompt))
    Console.Write(update.Text);
```

**Ampelum (preserves the stream, adds field-aware updates):**

```csharp
await foreach (var u in p.StreamAsync(new Qa { Question = question }))
{
    if (!u.IsFinal) Console.Write(u.PartialText);
    else            Console.WriteLine($"\n[final answer: {u.Value!.Answer}]");
}
```

## Cross-cutting concerns

Hooks attach via `AmpelumContext { Hooks = ... }` — no need to wrap
`IChatClient` in delegating clients:

```csharp
var ctx = new AmpelumContext(chat, new ChatAdapter(), traceSink, loggerFactory)
{
    Hooks = new IHook[]
    {
        new BudgetHook(Budget.Create().WithCallLimit(100).Build()),
        new RetryHook(maxAttempts: 3),
        new AuditHook(auditSink),
    },
};
```

## Things you keep

- `IChatClient` registration (OpenAI, Anthropic, Bedrock, local Ollama, …)
- `ChatOptions` (pass via `AmpelumContext.DefaultChatOptions`)
- Cancellation and `IHttpClientFactory` integration
- `Microsoft.Extensions.Logging` (`AmpelumContext.LoggerFactory`)
