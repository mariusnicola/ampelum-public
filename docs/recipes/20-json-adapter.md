# JSON Adapter / Structured Output

`JsonAdapter` swaps the default `[[ ## Field ## ]]` marker protocol for a
strict JSON-object response. The adapter embeds a JSON Schema in the
system prompt, sets `ChatResponseFormat.ForJsonSchema(...)` on
`ChatOptions` (via the `IStructuredOutputAdapter` contract Predict honors
automatically), and parses the response into the typed signature record.
Malformed responses surface as `AdapterParseException`.

The recipe drives `Predict<MathQa>` first with a well-formed JSON payload,
then with a malformed one to demonstrate the exception path. `MathQa` is
the shared fixture (`Question` -> `Answer`).

```csharp
// ampelum:recipe structured-json-adapter
var fake = new FakeChatClient();
fake.EnqueueText("""{"answer":"42"}""");
fake.EnqueueText("not json at all");

var ctx = new AmpelumContext(fake, new JsonAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
var p = new Predict<MathQa>(ctx);

// Happy path: strict JSON parses straight into MathQa.Answer.
var r = await p.ForwardAsync(new MathQa { Question = "2+2*20?" }, ct: ct);
r.Value.Answer.ShouldBe("42");

// Malformed path: the second enqueued response is not JSON.
// JsonAdapter.Parse throws AdapterParseException, which propagates out of ForwardAsync.
var ex = await Should.ThrowAsync<AdapterParseException>(
    async () => await p.ForwardAsync(new MathQa { Question = "what?" }, ct: ct));
ex.AdapterName.ShouldBe(nameof(JsonAdapter));
ex.RawResponse.ShouldBe("not json at all");

// PascalCase is also accepted — this matches LM responses that don't
// follow the camelCase convention emitted in the schema.
var lenient = new JsonAdapter(strict: false);
var meta = SignatureMetadataCache.For<MathQa>();
var prose = "Here is your answer: {\"Answer\":\"42\"} thanks!";
var outs = lenient.Parse(meta, new Microsoft.Extensions.AI.ChatResponse(
    new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, prose)));
outs["Answer"].ShouldBe("42");
```

Expected: the first forward parses `{"answer":"42"}` into
`MathQa.Answer == "42"`; the second throws `AdapterParseException`
with `AdapterName == "JsonAdapter"` and `RawResponse` echoing the malformed
payload. The lenient (`strict: false`) constructor extracts JSON from
surrounding prose with a span search.
