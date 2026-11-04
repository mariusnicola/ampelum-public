# Composed programs: release notes

Drafts and scores product release notes from a CSV of changes. Each change is classified, drafted by a
strategy that depends on its kind and impact, tightened in a loop, and scored. The notes are then
reviewed by a panel of agents, fact checked, announced by a task agent, and the program can be measured
and tuned against the approved notes of the previous release.

## Run

From the repository root:

```
dotnet restore samples/Ampelum.Samples.ComposedPrograms/Ampelum.Samples.ComposedPrograms.csproj --nologo
dotnet build samples/Ampelum.Samples.ComposedPrograms/Ampelum.Samples.ComposedPrograms.csproj -c Release --no-restore --nologo
dotnet run --project samples/Ampelum.Samples.ComposedPrograms/Ampelum.Samples.ComposedPrograms.csproj -c Release --no-build -- --fake
```

Without `--fake` the program takes the chat client from the shared sample configuration and runs the
release only. Three flags add stages, because each makes many model calls:

- `--evaluate` scores the classifier and the plain drafting call against the approved notes with the
  metrics named in the options.
- `--tune` runs GEPA on the approved notes, on a program that has one drafting call per kind of change, and
  prints the ROUGE-L of the baseline and of the tuned program. GEPA runs the program on the approved notes,
  shows the proposer each note next to the approved one, and keeps a pool of instruction sets; two of them that
  changed different calls are merged.
- `--extras` runs the stages below. They pick their change rows by classification, so editing
  `data/changes.csv` does not break them; they stop with a message if no change is classified as Internal or Fix.
  - one draft of an internal change with the impact bar lowered, so it goes through Refine;
  - the style search of one fix change in every retrieval mode;
  - a round trip of the style index: it is saved to a temporary file named `release-style-<guid>.json`
    in the temp folder, restored by a second pipeline, searched again, and the file is deleted;
  - the retrieval evaluation over `data/retrieval-eval.csv`;
  - a draft with nearest neighbour few shot, and a draft chosen by an ensemble.

`--fake` turns on `--evaluate`, `--tune` and `--extras` as well, and replaces the model with the scripted one.

## Test

```
dotnet restore tests/Ampelum.Samples.ComposedPrograms.Tests/Ampelum.Samples.ComposedPrograms.Tests.csproj --nologo
dotnet build tests/Ampelum.Samples.ComposedPrograms.Tests/Ampelum.Samples.ComposedPrograms.Tests.csproj -c Release --no-restore --nologo
dotnet test tests/Ampelum.Samples.ComposedPrograms.Tests/Ampelum.Samples.ComposedPrograms.Tests.csproj -c Release --no-build
```

The tests need no key, no network and no environment variable. Some write temporary files in the temp
folder and delete them.

## Settings

The live path reads only `OPENROUTER_API_KEY` and `OPENROUTER_MODEL_1`, through the shared sample
configuration. `.sample.env` names them. No other setting is read, and no key, endpoint or model id is
written in this sample.

Tunables are in `ReleaseNotesOptions`: word limit, tolerance factor, pass limit, stalled epsilon, impact
tolerance, fact check steps, impact bar, strategy per kind, candidates, refine threshold and limit,
audiences, tone, retries and retry delay, tag fields, style top k, retrieval mode, score floor, chunk size,
token budget, index path, panel turns and memory sizes, speaker selector, neighbour count, queued tracing,
trace file and the metric names.

These are fixed in code, not options:

- the loop bound, equal to the pass limit (`TightenStage`);
- the number of paraphrases (3) and sub questions (2) in `StyleRetrieval`, and the rewrite and router prompts;
- the panel roster and roles (`ReviewPanel`) and which notes are fact checked (breaking ones);
- the signatures and their instructions, the judge instructions and the set of registered metrics;
- the data file names, and the GEPA options (`PromptTuning.DefaultOptions`), which are the library defaults except a minibatch of all ten approved notes, a merge turn every second iteration and a call budget of 400.

## What `--fake` proves, and what it does not

`--fake` replaces the model with `Fake/ScriptedModel.cs`: rules that read the prompt and return text.
It needs no key and no network.

It proves that the wiring runs end to end through the real library types: the right module is called
for each change, the fields the model is asked for are in the prompt, answers in each wire format
(plain fields, XML, JSON, tool calls) are parsed, the stop rules stop, scores come from the shipped
metric code, and trace and log output is produced. The tests count model calls and read the requests
the scripted model received.

It does not prove anything about model quality. The notes, the classifications, the review verdict, the
judge scores and the tuned instruction are written by the rules in `Fake/`, so they say nothing about
what a real model writes. The tuning figure (ROUGE-L 0.326 to 0.540 for the default seed) is scripted under `--fake`, and it
is measured on the same approved notes that GEPA trained and validated on, so it is not a held-out result.
The scripted tuner has one good instruction for every call. A change of one kind only changes the notes of
that kind, so candidates are best on different notes and a merge of two siblings is possible. Which parent
GEPA draws is random for the seed, and a run in which every parent descends from the previous child never
has two siblings: of seeds 0 to 99, six (11, 31, 42, 66, 74, 81) end without a merge, and the tests cover
seeds 0 to 9. Whether a real model's proposals are merged depends on what it writes.

The live path (a real model through `SampleConfig`) has not been run.

## Notes for the live path

- `KindParser.Parse` throws on a kind that is not Feature, Fix, Breaking or Internal (case is ignored),
  and the classify call is not inside an ErrorHandler. One off schema classification reply therefore
  aborts the release run. The drafting calls do have retries and a fallback.
- `ReAct` (the fact check) and `TaskAgent` (the announcement) call tools, so they need a model that supports
  tool calling.
- The XML classify step and the two step headline need a model that can follow the format; a weak model
  may fail strict XML parsing.

## What only the tests exercise

A default `--fake` run does not use the model driven speaker selector (`PanelPicksSpeakerWithModel` is
off), and the episodic memory tier never reaches its summary threshold. Tests turn both on and assert the
extra model calls. `AnnouncementResult.Streamed` and `Status` are not printed by the program; a test
asserts both.

## Feature map

| Feature | Where it runs |
|---|---|
| Sequence | `Drafting/NoteDrafter.cs:52` |
| ConditionalBranch | `Drafting/NoteDrafter.cs:80` |
| Loop | `Drafting/TightenStage.cs:88` |
| Loop predicates, CompositeAll, CompositeAny, LoopPredicateRegistry | `Drafting/TightenStage.cs:33` to `:37`, `:72` |
| ErrorHandler | `Drafting/DraftingModules.cs:43` |
| WithConfig | `Drafting/DraftingModules.cs:42` |
| Lazy | `ReleaseNotesPipeline.cs:128` |
| BestOfN | `Drafting/DraftingModules.cs:26` |
| Refine | `Drafting/DraftingModules.cs:27` |
| MultiChainComparison | `Drafting/DraftingModules.cs:29` |
| Parallel | `Drafting/DraftingModules.cs:30` |
| ChainOfThought | `Drafting/DraftingModules.cs:20` |
| DynamicPredict | `Drafting/ChangeTagger.cs:25` |
| XMLAdapter | `Drafting/NoteDrafter.cs:64` |
| TwoStepAdapter | `ReleaseNotesPipeline.cs:129` |
| Shipped metrics and registry | `Evaluation/MetricCatalog.cs:39` |
| CsvDataset | `ChangeSource.cs:16`, `:18` |
| EvalReport.Render | `Evaluation/ReleaseEvaluation.cs:46` |
| TaskAgent | `Assistant/AnnouncementWriter.cs:25` |
| Ensemble | `Tuning/PromptTuning.cs:78` |
| GEPA | `Tuning/PromptTuning.cs:39` |
| KnnFewShot | `Tuning/PromptTuning.cs:62` |
| Trace sinks (in memory, logging, batched, synchronous and queued registration) | `ReleaseNotesServices.cs:43`, `:45`; `Observability/ReleaseTracing.cs:45`, `:67` |
| OtelLogsBridge, ExporterDiagnostics | `Observability/ReleaseTracing.cs:87`, `:37` |
| SubagentOrchestrator | `ReleaseNotesPipeline.cs:93` |
| GroupChat, selectors | `Review/ReviewPanel.cs:38`, `:35`, `:36` |
| Memory tiers, AddConversationMemory | `Review/ReviewPanel.cs:57`, `:58`, `:59`; `ReleaseNotesServices.cs:50` |
| Agent tools (IAgentTool, registry), ITypedTool, IReActTool | `Review/ChangeTools.cs`, `Review/ReviewAgents.cs` |
| ReAct | `Review/FactCheck.cs:11` |
| Retrieval modes (rewrite, HyDE, multi query, step back, sub questions, router) | `Style/StyleRetrieval.cs:29` to `:34` |
| ScoreThresholdRetriever, FilterPredicate | `Style/StyleRetrieval.cs:24`, `:39` |
| Loaders and splitters | `Style/StyleIndex.cs:36` to `:48`, `Style/StyleMemory.cs:66` |
| Index save and restore | `Style/StyleIndex.cs:77`, `:85` |
| EmbeddingCache | `Style/StyleMemory.cs:34` |
| RetrievalEvalHarness | `Style/StyleMemory.cs:73` |

Of the optimizers, only GEPA, KnnFewShot and Ensemble are used here. The other optimizers are shown in the
prompt tuning sample and the traced pipeline sample.

## Not covered

- A sub agent runner for coding agents with file and shell tools: nothing in a release notes job needs it.
- A remote agent client: it needs a remote agent endpoint, which means a new setting.
- Cohere and Voyage rerankers: they need third party keys that `.sample.env` does not name.
- A PDF loader: this use case has no PDF.
- The hybrid retriever and the reciprocal rank fusion reranker: the style index is small and searched by
  one embedding, so there is no second ranking to fuse, and nothing in the use case needs a lexical index.

## Known limits of the library

No test pins a flaw any more. `tests/Ampelum.Samples.ComposedPrograms.Tests/KnownLimitTests.cs` holds the tests that
show the fixed behaviour (a `Loop` counts body runs as iterations and keeps the predicate's own stop reason, even on
the check after the last allowed run). What is left:

- A reloaded index hands a string, a bool and null back as plain values, and a number, array or object as a
  `JsonElement` (so a save after a reload writes the same bytes, whatever the number type was). A store side
  equality filter matches a reloaded document (`A_reloaded_index_still_matches_a_store_side_metadata_filter`);
  numbers match by value, so a filter written with the stored value and type finds them (`long` 5, double 5.0,
  decimal 1.10, `ulong` max, `short` 7, and so on), and so does an `int` 5. Not matched: a `float` stored as
  0.1f against a filter written as the double `(double)0.1f` (0.10000000149011612), because the file holds the
  text "0.1". Metadata you read back from a reloaded index is a `JsonElement` for numbers, so read it with
  `GetInt32()`, `GetDouble()` and so on.

A bare `Predict` ignores an instruction override that `OptimizedProgram.Bind` gives it: `Bind` sets no
submodule name for a program that is not an `IComposedModule`, and `Predict` only reads an override under its
submodule name (`src/Ampelum/Optimizers/OptimizedProgram.cs`, lines 93 to 97, and `src/Ampelum/Predict.cs`,
lines 150 to 156; checked with a throwaway test that bound an override under the name GEPA uses). GEPA's
result for such a program therefore changes nothing. That is why the sample tunes a program with named submodules.
