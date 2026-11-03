# Prompt tuning sample

Measures a prompt on labeled data, improves it, and measures again.

The task is customer feedback sentiment (positive, negative, neutral). The labeled rows live in
`data/feedback.jsonl`. The flow, all in `PromptTuningPipeline.cs`:

1. Evaluate the untuned prompt with a label match metric (`LabelMatch.cs`) that ignores case and
   surrounding whitespace.
2. Optimize with `MiproV2` and an explicit seed (`OptimizerSeed`). It bootstraps demo sets from the runs that pass the metric, asks the
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

## SIMBA, BootstrapFinetune and BetterTogether

`OptimizerTour.cs` runs the three other optimizers on the same task, one method each. They run only under
`--fake`, after the flow above: the library ships no fine-tuning provider, so there is nothing to run
`BootstrapFinetune` or `BetterTogether` against live. Each method scores the untuned program, optimizes it
and scores the result on the same rows, so the scores say that the flow works, not that anything generalizes.

- `RunSimbaAsync` runs `SimbaOptimizer` with a seed. `WobblingChatClient.cs` answers a row wrong the first
  time and then alternates, as a sampling model does, and answers right every time when the prompt carries
  the tuned instruction or a demo. `ScriptedRuleClient.cs` writes that instruction as the rule. SIMBA finds
  the unstable rows, builds a candidate from them and keeps the best on the whole dataset.
- `RunBootstrapFinetuneAsync` runs `BootstrapFinetune`. The scripted classifier of the flow above answers the
  first three rows right, so those three runs are the training data that reaches `ScriptedFineTuneProvider.cs`,
  which returns a classifier that answers seven rows right. The tuned program calls that client.
- `RunBetterTogetherAsync` runs `BetterTogether` with `p -> w -> p`: demos, then the fine-tuning provider,
  then demos again. It scores each step and returns the best. The third step's search runs on top of the
  first two: `OptimizedProgram.Bind` keeps the tuned client and the demos a module is already bound to.
  The sample's program is a composed program, so it uses the per-submodule binding; the single-module
  binding is covered by the library tests only.

What `--fake` proves: the optimizers are wired as the code says, the provider gets the runs that passed the
metric in chat format, the tuned client is the one the tuned program calls, and the steps run in order. What it
does not prove: that a fine-tuned model improves, or that SIMBA rules or demos help a real model.

## Run against a model

Without `--fake` the sample reads `OPENROUTER_API_KEY` and `OPENROUTER_MODEL_1` from `.env`
(copy `.sample.env` and fill them in) or from environment variables. The same client classifies
and proposes. The live path has never been run.

## Library defects met

- The trace a `Predict` writes goes to the sink of the context it was bound with, not to the sink
  `BootstrapFewShot` puts on the context it hands the runner, so the runner in
  `PromptTuningPipeline.cs` forwards `prediction.Trace` by hand.
