# Ampelum Researcher Template

Console app showcasing `Predict<TSig>` + a hook for `BootstrapFewShot` over a
JSONL training set, with notebook-friendly markdown output via Spectre.Console.

## Quickstart

```bash
dotnet new ampelum-researcher -n Acme.Research
cd Acme.Research
dotnet run
```

Out of the box this runs against `FakeChatClient` (deterministic offline
behaviour). Swap to a real `IChatClient` from MEAI to run live.

## BootstrapFewShot (next step)

Load `data/train.jsonl` into `IDataset<SummariseSignature>` and pass it to
`BootstrapFewShot.Compile(predict, dataset, metric)` to learn demonstrations.
See `docs/recipes/` (when v1.0 ships the cookbook) for an end-to-end walk-through.

## Files

- `Program.cs` — entrypoint with notebook-style Markup output.
- `Signatures/SummariseSignature.cs` — one-input, one-output signature.
- `data/train.jsonl` — 3-row stub training set.
