# Migrating from LangChain (.NET / Python) to Ampelum

LangChain composes prompts as **Runnables** (chains of LCEL operators) and
threads tool-calling through agent executors. Ampelum's mental model is
**signature + module**: declare the I/O schema once, pick a module
(`Predict`, `ChainOfThought`, `ReAct`), and the prompt formatting and parsing
are derived from the schema.

## Single Predict

**LangChain (Python):**

```python
prompt = ChatPromptTemplate.from_template("Q: {question}\nA:")
chain  = prompt | llm | StrOutputParser()
answer = chain.invoke({"question": "Capital of France?"})
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
var p   = new Predict<Qa>(ctx);
var r   = await p.ForwardAsync(new Qa { Question = "Capital of France?" });
string answer = r.Value.Answer;
```

## Chain-of-thought

LangChain composes CoT as a prompt-template appendix
("Let's think step by step ..."); the `rationale` is a free-form sub-string
of the response. Ampelum elevates `reasoning` into the parsed schema:

```csharp
var cot = new ChainOfThought<Qa>(ctx);
var r   = await cot.ForwardAsync(new Qa { Question = "..." });
string reasoning = r.Reasoning();
string answer    = r.Value.Answer;
```

## Tools / agents

**LangChain:**

```python
tools = [DuckDuckGoSearchRun(), Calculator()]
agent = create_react_agent(llm, tools, prompt)
```

**Ampelum:**

```csharp
var tools = new ITypedTool[] { new WebSearchTool(...), new CalculatorTool() };
var react = new ReAct<MathSig>(ctx, tools, maxIterations: 6);
var r     = await react.ForwardAsync(new MathSig { Question = "..." });
```

`ITypedTool<TResult>` is the Ampelum-native tool surface; the
`Ampelum.Tools.*` packages ship Calculator, WebSearch, Wikipedia, and
CodeInterpreter implementations.

## Optimizer

LangChain has no built-in prompt optimizer. Ampelum compiles the program:

```csharp
var compiled = await new BootstrapFewShot(ctx).CompileAsync(p, trainset, metric, ct: ct);
await JsonProgramPersistence.SaveAsync(compiled, "compiled.json", ct);
```

## Streaming

```csharp
await foreach (var update in p.StreamAsync(new Qa { Question = "..." }))
    if (!update.IsFinal) Console.Write(update.PartialText);
```

## Hooks

LangChain uses callbacks (`BaseCallbackHandler`); Ampelum uses ordered hooks:

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

`OnBefore` runs ascending by `Order`; `OnAfter` and `OnError` descending.

## Things you keep

- `IChatClient` registration — any MEAI provider (OpenAI, Anthropic, …)
- DI / logging / cancellation / `HttpClientFactory`
- Vector store backends — see `Ampelum.VectorStores.{PgVector,Pinecone,Qdrant}`
