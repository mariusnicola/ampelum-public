# Prompt tuning sample

Measures a prompt on labeled data, improves it, and measures again.

The task is customer feedback sentiment (positive, negative, neutral). The labeled rows live in
`data/feedback.jsonl`. The flow, all in `PromptTuningPipeline.cs`:

1. Evaluate the untuned prompt with a label match metric (`LabelMatch.cs`) that ignores case and
   surrounding whitespace.
2. Optimize with `MiproV2` and an explicit seed (`OptimizerSeed`). It bootstraps demo sets, asks the
   proposer client for a dataset summary and instruction candidates, and searches the combinations
   with a seeded Tree-structured Parzen Estimator on minibatches. The program prints the chosen
   instruction and how many demos came with it.
3. Save the tuned program with `JsonProgramPersistence`, load it back and bind it.
4. Evaluate again and print an A/B comparison report.

The optimizer scores the same rows it trains on, because the sample has no held-out set. The score
shows that the flow works, not that the prompt generalizes. Pass a `valset` to `MiproV2` for that.

## Run with scripted replies

```
dotnet run --project samples/Ampelum.Samples.PromptTuning -c Release -- --fake
```

`--fake` needs no key and no network. Two scripted clients stand in for the model: one classifies
(`ScriptedChatClient.cs`), one plays the instruction proposer (`ScriptedProposerClient.cs`). The
classifier answers the first N rows right, with N chosen by the instruction in the prompt and by
whether the prompt carries demos. So the scores come from the script, not from any model, and the
instruction and demos `--fake` picks say nothing about real prompts. What it does prove: the flow
runs end to end, the proposer is called with a summary and grounded proposals, the chosen
instruction and demos are saved, reloaded and sent to the model, and the same seed gives the same run.
Exit code 0 means the scenario succeeded.

## Run against a model

Without `--fake` the sample reads `OPENROUTER_API_KEY` and `OPENROUTER_MODEL_1` from `.env`
(copy `.sample.env` and fill them in) or from environment variables. The same client classifies
and proposes. The live path has never been run.

## Library defects met

- `BootstrapFewShot` keeps every successful run as a demo, correct or not, so bootstrapped demos can
  hold wrong labels (`src/Ampelum/Optimizers/BootstrapFewShot.cs`, lines 79 to 87 and 102 to 105).
  `MiproV2` inherits this; its search then prefers demo sets that score well.
- The trace a `Predict` writes goes to the sink of the context it was bound with, not to the sink
  `BootstrapFewShot` puts on the context it hands the runner, so the runner in
  `PromptTuningPipeline.cs` forwards `prediction.Trace` by hand.
