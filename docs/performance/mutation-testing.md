---
date: 2026-04-26
milestone: v1.0-rc1
gate: G2 (mutation testing ≥85%)
---

# Mutation Testing — Stryker.NET

v1.0-rc1 readiness gate **G2** requires Stryker mutation kill-score ≥85% across
the `[Ampelum]*` + `[Ampelum.Testing]*` assemblies.

## Configuration

Pinned in [`stryker-config.json`](../../stryker-config.json):

| Setting | Value | Rationale |
|---|---|---|
| `project` | `src/Ampelum/Ampelum.csproj` | Primary library under mutation. |
| `test-projects` | `tests/Ampelum.Tests`, `tests/Ampelum.Testing.Tests` | Authoritative coverage harness; excludes ContractTests / Recipes / Templates which are scoped to integration surface and run separately. |
| `mutate` | `src/Ampelum/**/*.cs`, `src/Ampelum.Testing/**/*.cs` (excl. `*.g.cs`, `Generated/**`) | Mutates the entire shipped surface; excludes source-generator output (`*.g.cs`) and recipe-extracted code (`Generated/**`) which are derived artefacts. |
| `ignore-mutations` | `string`, `regex` | String literals are exception-message text; regex mutations dominate noise. |
| `thresholds.break` | `85` | CI gate: kill-score below 85% fails the build. |
| `thresholds.low` | `85` | Floor before the report colours yellow. |
| `thresholds.high` | `95` | Aspirational ceiling for green. |
| `concurrency` | `4` | Bounded to keep nightly runner under 90 min. |

## CI

CI does not run Stryker. Run it locally with the command below.

## Local run

```bash
dotnet tool install -g dotnet-stryker  # or `update -g`
dotnet stryker --config-file stryker-config.json
```

Expected wall-clock on a 4-core developer machine: 30–60 minutes for the full
sweep, longer on cold caches. The HTML report at
`StrykerOutput/<timestamp>/reports/mutation-report.html` ranks survivors by file
and provides reproducer hints.

### Local-run blockers encountered (alpha.8 baseline)

The first local Stryker run as part of E1/G2 sign-off hit two environmental
issues on Windows + .NET 10:

1. **Stryker 4.4.1 (auto-installed default) ships an older Roslyn than the
   `[GeneratedRegex]` source generator emits.** The regex partial method
   `private static partial Regex Boundary()` in `SentenceSplitter.cs` triggers
   a "must have an implementation part because it has accessibility modifiers"
   compile error inside Stryker's mutation compile step. **Fix:** upgrade to
   Stryker 4.14.1 (`dotnet tool update -g dotnet-stryker`) which bundles
   compiler 4.14, matching the SDK's analyzer.
2. **Stryker 4.14.1 in solution-mode auto-discovers every test project in
   `Ampelum.sln`, including `Ampelum.Recipes.Tests` whose `Program.cs` is
   produced at build time by the recipe extractor and not committed.** The
   `test-projects` whitelist in `stryker-config.json` does not override
   solution-mode discovery. **Fix:** run Stryker in project-mode by invoking
   it from the source-project directory with a config that uses paths
   relative to that directory, e.g. `StrykerLogs/stryker-local.json`:

   ```bash
   cd src/Ampelum
   dotnet stryker --config-file ../../StrykerLogs/stryker-local.json
   ```

The CI workflow (Linux runner, no Recipes.Tests build dependency) does not
hit either issue.

## Sign-off

| Date | Kill score | Survivors | Run mode | Reviewer |
|---|---|---|---|---|
| 2026-04-26 (alpha.8) | TBD — run in progress at tag time | TBD | local project-mode | Albert / Claude |

**Status at v1.0-rc1.alpha.8 tag:** Stryker 4.14.1 is running on the
project-mode config against `Ampelum.csproj` + `Ampelum.Testing.dll`
(4797 tests). The initial test run completed; mutation phase is in flight.
The kill-score will be appended to this table in the post-tag follow-up.

The previous narrow-scoped config (Optimizers/Audit/Hooks only) ran clean —
broadening to the full shipped surface is expected to surface 5–15% additional
survivors which will be triaged between alpha.8 and rc1 final.
