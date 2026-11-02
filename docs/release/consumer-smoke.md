# Consumer-side smoke test (v1.0-rc1.alpha.10)

This document records the first end-to-end install + use exercise of the
published `_packs/` against a brand-new downstream consumer csproj. The
exercise surfaced two real, community-blocking pack-metadata bugs that were
fixed before tagging alpha.10.

## What it covers

`tests/ConsumerSmoke.Tests/` is a sibling test project deliberately kept out
of the regular coverage gate (no `Ampelum.*` prefix; sentinel
`Directory.Build.props` / `Directory.Build.targets` / `Directory.Packages.props`
stop MSBuild from picking up the repo's central package management or shared
NuGet metadata). Its `nuget.config` clears default sources and adds `_packs/`
as the primary feed, so package resolution exercises the same shape a
downstream user gets.

It references five packs by version (no `ProjectReference`):

- `Ampelum 0.8.0-rc1`
- `Ampelum.Testing 0.8.0-rc1`
- `Ampelum.Analyzers 0.8.0-rc1`
- `Ampelum.SourceGenerator 0.8.0-rc1`
- `Ampelum.Tools.Calculator 0.8.0-rc1`

Four xUnit smoke tests run inside that consumer compilation:

| Scenario | What it proves |
| --- | --- |
| `HelloPredictSmoke.Predict_Forward_ReturnsParsedValue` | Core API surface (`AmpelumContext`, `Predict<TSig>`, `ChatAdapter`) and the `FakeChatClient` from `Ampelum.Testing` ship and round-trip a parsed value end-to-end. |
| `SourceGenSmoke.For_ReturnsGeneratedMetadata_ForConsumerSignature` | The source-generator nupkg's `analyzers/dotnet/cs/` payload runs inside a consumer compilation, emits the `[ModuleInitializer]`, and `SignatureMetadataCache.For<T>` resolves cleanly. |
| `AnalyzerPackSmoke.AnalyzerPack_ShipsAtCanonicalPath` | The published `Ampelum.Analyzers.0.8.0-rc1.nupkg` ships a DLL at the canonical NuGet-recognised path `analyzers/dotnet/cs/Ampelum.Analyzers.dll`. |
| `AnalyzerPackSmoke.PackedAnalyzer_FiresAmp1010_OnFreeFormWithoutOptIn` | The DLL inside that nupkg is loaded in-process and run against a tiny `CSharpCompilation` that violates POLC. AMP1010 surfaces as a Warning. |

## Bugs found and fixed during this exercise

### Bug 1 — Analyzers/SourceGenerator packs missing version + license metadata

`src/Ampelum.Analyzers/Ampelum.Analyzers.csproj` and
`src/Ampelum.SourceGenerator/Ampelum.SourceGenerator.csproj` did not set
`<IsPackable>true</IsPackable>`. The shared metadata block in
`Directory.Build.targets` is gated on that condition, so packs were created
**without** the explicit `<Version>`, `<PackageLicenseExpression>MIT</…>`,
`<RepositoryUrl>`, `<PackageProjectUrl>`, or readme metadata. They shipped as
`Ampelum.Analyzers 1.0.0` (default `Version` from MSBuild) with `authors=Ampelum.Analyzers`
and no license expression — a NuGet validator on a strict feed would reject
them.

**Fix:** added `<IsPackable>true</IsPackable>` to both csprojs and (for the
analyzer csproj only) `<NoWarn>$(NoWarn);CS1591</NoWarn>` because turning on
`IsPackable` activates the shared `<GenerateDocumentationFile>true</…>` rule.
The analyzer's public surface is its diagnostic IDs, not its class members,
so doc-comments on the `DiagnosticAnalyzer` overrides aren't required.

### Bug 2 — Sister packs declared a stable dep on a prerelease core (NU1605)

Every sister pack except the CLI (14 packs) declared
`<dependency id="Ampelum" version="0.8.0" exclude="Build,Analyzers"/>` in its
nuspec. Only `Ampelum 0.8.0-rc1` ships, so `dotnet restore` of any consumer
that referenced both `Ampelum` and `Ampelum.Testing` (or any other sister)
hit:

> error NU1605: Detected package downgrade: Ampelum from 0.8.0 to 0.8.0-rc1.

This was the bug that `dotnet restore` of the consumer-smoke project
surfaced on the first run — exactly the community-blocking class of bug the
smoke exercise was built to catch.

**Fix:** moved the version definition into `Directory.Build.targets`:

```xml
<Version Condition="'$(Version)' == '' or '$(Version)' == '1.0.0'">0.8.0-rc1</Version>
```

and dropped the explicit `<Version>` overrides from the three csprojs that
had been pinning either `0.8.0` or `0.8.0-rc1`. After repacking from a clean
`_packs/`, every pack ships at `0.8.0-rc1` and every sister-pack's nuspec
declares `<dependency id="Ampelum" version="0.8.0-rc1" />`. The whole pack
set transitively resolves without downgrade.

The stable-depends-on-prerelease NU5104 that originally motivated keeping
core at `-rc1` and sisters at stable was the wrong escape hatch — it traded
NU5104 (caught at pack time, only on author's machine) for NU1605 (caught at
restore time, on every consumer's machine). Promoting the whole milestone to
`-rc1` is the right SemVer answer for a milestone that has any prerelease
transitive dep.

## Working consumer surface

After the two fixes, a fresh consumer csproj that lists the five packs
above:

- restores cleanly (no NU1605, no NU5104);
- builds with the source generator running and emitting metadata
  initializers for consumer-declared `[Signature]` records;
- has the analyzer wired in (we asserted this by reading the nupkg layout
  and exercising the analyzer DLL directly — but the same `analyzers/dotnet/cs/`
  path is what NuGet wires into a regular consumer build);
- exercises `Predict<T>` end-to-end with `FakeChatClient` and parses ChatAdapter
  output correctly.

This is the green-baseline scenario the README's "getting started" walkthrough
will assume from alpha.10 onward.

## What is NOT covered

- The 14 other sister packs (vector stores, observability sinks, tool packs
  beyond Calculator, MCP server, REPL, CLI, templates, recipes). Restore
  resolution covers them transitively because they all pin the same Ampelum
  core, but a per-pack functional smoke (e.g. PgVector handshake, Phoenix
  span emit) is deferred. The pack-metadata fix in this alpha is structural
  and applies uniformly, so the risk of one of those packs misbehaving
  *because of metadata* is negligible. Functional risk on those packs
  remains and will be covered by their own integration test suites.
- A real downstream consumer running on Linux. The smoke project runs on
  the same Windows host as the rest of the suite. NuGet pack resolution is
  platform-agnostic; Linux smoke is deferred to CI's existing AOT-smoke run
  on linux-x64.
