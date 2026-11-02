# Prompt tuning sample

Measures a prompt on labeled data, improves it, and measures again.

The task is customer feedback sentiment (positive, negative, neutral). The labeled rows live in
`data/feedback.jsonl`. The flow:

1. Evaluate the untuned prompt with a label match metric that ignores case and surrounding whitespace.
2. Optimize the instruction with `MiproV2`, which scores every candidate by running the program.
3. Save the tuned program with `JsonProgramPersistence`, load it back and bind it.
4. Evaluate again and print an A/B comparison report.

## Run with scripted replies

```
dotnet run --project samples/Ampelum.Samples.PromptTuning -c Release -- --fake
```

`--fake` needs no key. It uses a scripted `FakeChatClient`, so the scores come from the script and
are not real measurements. Use it to see the flow, not to judge a prompt.

## Run against a model

Without `--fake` the sample reads `OPENROUTER_API_KEY` and `OPENROUTER_MODEL_1` from `.env`
(copy `.sample.env` and fill them in) or from environment variables.
