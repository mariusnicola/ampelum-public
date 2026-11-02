# Ampelum Agent Template

Two-agent GroupChat (Researcher + Critic) with a typed-tool stub.

## Quickstart

```bash
dotnet new ampelum-agent -n Acme.Agent
cd Acme.Agent
dotnet run
```

## What's wired

- `Agents/Researcher.cs` — drafts a response.
- `Agents/Critic.cs` — reviews and approves.
- `Tools/CalculatorTool.cs` — `ITypedTool<double>` stub. Wire into a tool
  registry in your real app and let the Researcher invoke it.
- `Program.cs` — runs `GroupChat.RunAsync` with `RoundRobinSelector` and a
  4-turn budget.

## Notes

The agent surface is `[Experimental("AMPELUM_V09_PREVIEW")]` while the v0.9
preview shakes out — the warning is suppressed in the csproj. Remove the
`NoWarn` once Ampelum 1.0 ships.
