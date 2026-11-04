# Traced pipeline sample

Evaluates one classifier tuned by several optimizers on the same labeled rows, and exports every
evaluation trace to LangFuse, LangSmith and Phoenix.

The task is customer feedback sentiment. The labeled rows live in `data/feedback.jsonl`. The flow:

1. Compile the program with `BootstrapFewShot`, `LabeledFewShot`, `BootstrapFewShotWithRandomSearch`,
   `Copro` and `InferRules`. Optimizer runs export nothing.
2. Evaluate the untuned program and each tuned program on every row. These evaluations are the traces
   that get exported: one trace per row and variant.
3. Print the variants as a table.

## Run with scripted replies

```
dotnet run --project samples/Ampelum.Samples.TracedPipeline -c Release -- --fake
```

`--fake` needs no key and no network. A scripted chat client answers, and every exporter request goes to
an in-memory `HttpMessageHandler` that records it. The scores come from the script, are labeled as
scripted in the output, and are not real measurements. Use `--fake` to see the flow.

## Run against a model

Without `--fake` the sample reads its settings from `.env` (copy `.sample.env` and fill them in) or from
environment variables. Live runs send traces to the real endpoints.

Chat: `OPENROUTER_API_KEY`, `OPENROUTER_MODEL_1`.

Destinations, all required except the last:

- `LANGFUSE_PUBLIC_KEY`, `LANGFUSE_SECRET_KEY`, `LANGFUSE_ENDPOINT`
- `LANGSMITH_API_KEY`, `LANGSMITH_PROJECT`, `LANGSMITH_ENDPOINT`
- `PHOENIX_ENDPOINT`
- `PHOENIX_API_KEY`, optional: leave it out for a local collector without auth

## Things to know

- Phoenix: `AddPhoenixExporter` only emits spans on an `ActivitySource`. It makes no network call and
  never reads `PhoenixCredentials`. The sample adds the OpenTelemetry SDK with an OTLP exporter and
  sets the endpoint and key there.
- `BootstrapFewShot` never calls the metric. It only collects demos from the program's own traces.
- `BootstrapFewShot` over a program that was never bound fails: its runner throws on every example, and `CompileAsync` throws an `OptimizerException` naming the count and the first error instead of returning labeled demos with no sign of the cause (`BootstrapFewShot_over_an_unbound_program_fails_naming_the_cause_instead_of_returning_no_demos`). The sample binds the program first.
- `BootstrapFewShotWithRandomSearch` and `Copro` score a candidate against itself when no scoring runner
  is given, so every candidate gets 1.0. The sample always passes one.
- Those optimizers hand the metric a placeholder `SignatureMetadata`. `LabelMatch` ignores it.
- Each exporter lives in its own service provider. Registering all three on one provider chains them,
  and disposing that provider only drains the outermost exporter.

## Tests

```
dotnet test tests/Ampelum.Samples.TracedPipeline.Tests -c Release
```
