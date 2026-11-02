---
date: 2026-04-26
milestone: v1.0-rc1
gate: G4 (NuGet publish dry-run)
---

# NuGet Publish Dry-Run — v1.0-rc1

v1.0-rc1 readiness gate **G4**: confirm every packable library produces a
valid `.nupkg` with the metadata required for `nuget.org` publish, and patch
any csproj missing required fields.

## Procedure

```bash
rm -rf _packs && mkdir _packs
dotnet pack -c Release -o _packs --nologo
# OR per-project (preferred when src/Ampelum is mid-edit by a parallel agent):
dotnet pack <path/to/csproj> -c Release -o _packs --nologo --no-build
```

Validation per `.nupkg`:

- `*.nuspec` present, well-formed
- `id`, `version`, `authors`, `description`, `license`, `projectUrl`,
  `repository`, `readme`, `tags` populated
- `README.md` packed at root
- `LICENSE` packed at root
- Dependencies match expected graph; no unintended transitive deps
- For analyzers / source generators: dll under `analyzers/dotnet/cs`,
  `<DevelopmentDependency>true</DevelopmentDependency>`, no lib/ref
- For templates: `content/<template-name>/...` populated

## Run inventory — 2026-04-26 (v1.0-rc1.alpha.5 + S2-fix in flight)

18 of 18 packable projects produce a valid `.nupkg`. Listed alphabetically:

| Package | Version | Size (bytes) | Type | Status |
|---|---|---|---|---|
| Ampelum.Analyzers | 1.0.0 | 10,034 | Roslyn analyzer | ok |
| Ampelum.Cli | 0.8.0 | 16,026,245 | dotnet tool (self-contained) | ok (large by design — bundles `ampelum new` runtime) |
| Ampelum.Mcp | 0.8.0 | 9,373 | library | ok |
| Ampelum.Observability.LangFuse | 0.8.0 | 22,234 | library | ok |
| Ampelum.Observability.LangSmith | 0.8.0 | 20,167 | library | ok |
| Ampelum.Observability.Phoenix | 0.8.0 | 12,264 | library | ok |
| Ampelum.Repl | 0.8.0 | 11,605 | library | ok |
| Ampelum.Retrieval.Reranking | 0.8.0 | 12,783 | library | ok |
| Ampelum.SourceGenerator | 1.0.0 | 10,697 | source generator | **patched** — added `DevelopmentDependency`, `SuppressDependenciesWhenPacking`, analyzer-pack `<None>` |
| Ampelum.Templates | 0.8.0 | 19,995 | template pack (5 personas) | ok |
| Ampelum.Testing | 0.8.0 | 16,708 | library | ok |
| Ampelum.Tools.Calculator | 0.8.0 | 8,635 | library | ok |
| Ampelum.Tools.CodeInterpreter | 0.8.0 | 9,784 | library | ok |
| Ampelum.Tools.WebSearch | 0.8.0 | 11,354 | library | ok |
| Ampelum.Tools.Wikipedia | 0.8.0 | 10,234 | library | ok |
| Ampelum.VectorStores.PgVector | 0.8.0 | 14,768 | library | ok |
| Ampelum.VectorStores.Pinecone | 0.8.0 | 12,980 | library | ok |
| Ampelum.VectorStores.Qdrant | 0.8.0 | 14,266 | library | ok |
| **Ampelum (core)** | 0.8.0-rc1 | 322,656 | library | **ok** — packed at v1.0-rc1.alpha.7 (HEAD `0b34da8`) |

Total: 19 packs validated end-to-end. Core ships as `0.8.0-rc1` (prerelease)
because of the beta `Lucene.Net 4.8.0-beta00017` transitive — NU5104 forbids
prerelease deps in stable packs and Lucene.Net has no stable 4.x line. Sister
packs stay at `0.8.0` stable; none transitively pull Lucene.

## Patches applied this session

### `src/Ampelum.SourceGenerator/Ampelum.SourceGenerator.csproj`

Pre-patch the project failed `dotnet pack` with `NU5128` ("declared
dependencies for `netstandard2.0` but no lib/ref folder"). The csproj had
`IsRoslynComponent` + `IncludeBuildOutput=false` but lacked the source-generator
pack idiom (mirror of `Ampelum.Analyzers.csproj`):

```xml
<DevelopmentDependency>true</DevelopmentDependency>
<SuppressDependenciesWhenPacking>true</SuppressDependenciesWhenPacking>
<None Include="$(OutputPath)\$(AssemblyName).dll"
      Pack="true" PackagePath="analyzers/dotnet/cs" Visible="false" />
```

Post-patch the package builds clean, contains the generator dll under
`analyzers/dotnet/cs`, and declares no transitive `Microsoft.CodeAnalysis.*`
dependency (correctly suppressed per analyzer-packaging guidance).

## License sanity

The task description references `Apache-2.0`; the repo `LICENSE` is **MIT**
(verified at HEAD). All packs honour MIT via per-project `<PackageLicenseExpression>MIT</PackageLicenseExpression>` (inherited from
`Directory.Build.props`) and `LICENSE` is packed at root. If the project
relicenses to Apache-2.0 before v1.0 GA, `Directory.Build.props` and the
top-level `LICENSE` file are the two co-located edits required.

## Warnings observed (non-blocking)

- `NU5048` "missing a readme" on `Ampelum.Analyzers` and `Ampelum.SourceGenerator`
  during `dotnet pack` output, but per-`.nupkg` inspection confirms `README.md`
  IS packed (it's pulled in via the inherited `<PackageReadmeFile>` in
  `Directory.Build.props`). The warning appears to be a benign analyzer-pack
  edge case in the `NuGet.Build.Tasks.Pack` SDK target. Not promoted to error.

## Core pack closeout (v1.0-rc1.alpha.7)

Resolved at HEAD `0b34da8`. PublicAPI surface is stable post-S2 typed-slot
fix. Pack runs clean:

```bash
dotnet pack src/Ampelum/Ampelum.csproj -c Release -o ./_packs --nologo
# -> _packs/Ampelum.0.8.0-rc1.nupkg  (322,656 bytes)
```

Nuspec validation:

- 8 files in nupkg: `Ampelum.dll` (638 KB), `Ampelum.xml` (440 KB), `README.md`,
  `LICENSE`, `Ampelum.nuspec`, plus standard NuGet metadata.
- No `*.g.cs` source-gen artifacts leak.
- No test-only deps (Shouldly, xUnit, coverlet absent).
- 15 pinned deps, all under `targetFramework="net10.0"`, `exclude="Build,Analyzers"`.
- `repository type="git"` with `commit="0b34da8…"` populated.
- `license` MIT (expression), `readme` README.md, `projectUrl`, `tags` populated.

Patch applied this session:

- `Directory.Build.targets`: added `<RepositoryType>git</RepositoryType>` and
  made `<Version>` overridable per-csproj (`Version Condition="'$(Version)' == ''"`)
  so a single project can ship as prerelease without bumping the rest.
- `src/Ampelum/Ampelum.csproj`: pinned `<Version>0.8.0-rc1</Version>` to
  satisfy NU5104 (prerelease deps require prerelease pack).

## Original deferral note (resolved)

`Ampelum.csproj` (the core library) currently fails `dotnet pack` with
`RS0016`/`RS0017` PublicAPI analyzer errors — the parallel S2-fix agent is
mid-edit on `src/Ampelum/Hooks/IHook.cs` and `src/Ampelum/Predict.cs`,
introducing new public surface (`HookContext.{ModelId,PromptTokens,CompletionTokens,LmCallDurationMs}`)
that lands in `PublicAPI.Unshipped.txt` after the in-flight commit.

This is **not** a pack metadata issue. Once S2-fix tags `v1.0-rc1.alpha.6`,
re-run:

```bash
dotnet pack src/Ampelum/Ampelum.csproj -c Release -o _packs --nologo
```

The core package's metadata was previously verified at v1.0-rc1.alpha.4 and
has not regressed in any of this session's edits (no `src/Ampelum/**` writes
this session per gate scoping).

## Sign-off checklist (final pre-publish)

- [ ] `dotnet pack -c Release` produces 19/19 packs with zero errors
- [ ] `nuget verify` passes on every `.nupkg`
- [ ] Every nuspec contains `<repository commit="...">`
- [ ] Every nuspec lists `<readme>README.md</readme>`
- [ ] License expression is current (MIT today; Apache-2.0 if relicensed)
- [ ] No floating dependency versions (`NU1011` central-pkg-mgmt)
- [ ] AOT-compat warnings reviewed (Calculator/CodeInterpreter pure-compute;
      others surface dynamic load — annotate `RequiresUnreferencedCode` where
      reflection is used)
- [ ] Versions match the release tag for this milestone

The 18 packs produced this session form the v1.0-rc1 dry-run baseline; the
final publish run will add `Ampelum` core after the S2-fix agent ships
`v1.0-rc1.alpha.6`.
