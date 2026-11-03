# Build-time tools

A small project that shows the Ampelum source generator and the Ampelum analyzers at build time.

No model, key or network is needed. The program does not call a model, so there is no `--fake` mode. It prints the metadata the generator registered and reads one field through a generated getter. Exit code 0 means the getter returned the input.

## What it shows

- `Summarize.cs`: one signature with one input and one output.
- `Program.cs`: `[assembly: GenerateSignatureMetadata]` turns on the typed getters. Remove that line and the program exits 1 with "no generated accessors registered".
- `Ampelum.Samples.BuildTimeTools.csproj`: adds the generator and the analyzers as `Analyzer` items. A project that references Ampelum does not get them automatically.
- The build runs with `TreatWarningsAsErrors`, so the sample must compile with 0 warnings.

The tests are in `tests/Ampelum.Samples.BuildTimeTools.Tests`:

- `GeneratedCodeTests`: the generated types exist in the sample assembly, and the generated getter reads the input. `SignatureMetadata_MatchesDeclaredNamesAndDescriptions_ReflectionCheck` is a reflection check: it passes with the generator detached too, so it is not proof of generated code. Generator proof is the existence and getter tests, and the emission tests in `GeneratorEmissionTests`.
- `GeneratorEmissionTests`: runs the generator on a signature and checks the emitted text and that it compiles.
- `ToolRuleTests`, `ShapeRuleTests`, `CodeRuleTests`: every firing test asserts the rule id, the exact message (or a message prefix where the message contains a dash), and the span text with its line and column. Each rule also has a quiet case. AMP014 has two quiet cases: the opt-out attribute and the Agent assembly name. The `.editorconfig` keys for AMP010, AMP011 and AMP012 are passed in memory.

## Commands

Run from the repository root.

```
dotnet build src/Ampelum/Ampelum.csproj -c Release --nologo -v q
dotnet build src/Ampelum.Analyzers/Ampelum.Analyzers.csproj -c Release --nologo -v q
dotnet restore samples/Ampelum.Samples.BuildTimeTools/Ampelum.Samples.BuildTimeTools.csproj --nologo
dotnet build samples/Ampelum.Samples.BuildTimeTools/Ampelum.Samples.BuildTimeTools.csproj -c Release --no-dependencies --no-restore --nologo -v q
dotnet restore tests/Ampelum.Samples.BuildTimeTools.Tests/Ampelum.Samples.BuildTimeTools.Tests.csproj --nologo
dotnet build tests/Ampelum.Samples.BuildTimeTools.Tests/Ampelum.Samples.BuildTimeTools.Tests.csproj -c Release --no-dependencies --no-restore --nologo -v q
dotnet test tests/Ampelum.Samples.BuildTimeTools.Tests/Ampelum.Samples.BuildTimeTools.Tests.csproj -c Release --no-build
dotnet run --project samples/Ampelum.Samples.BuildTimeTools/Ampelum.Samples.BuildTimeTools.csproj -c Release --no-build
```

`--no-dependencies` skips the referenced projects, so the first two build commands must have run once to produce their output. The second `dotnet build` builds the test project itself, which `dotnet test --no-build` needs.

## Notes

- The sample types are in the global namespace. The generator writes unqualified type names into `SignalGrid.Ampelum.Generated`, so a namespace whose first segment is `Ampelum` does not build.
- Known analyzer bug, AMP012: under the default order `Controllers;Services;DataAccess` the rule flags a controller calling a service. That is the opposite of normal layering. `Amp012_KnownInversion_ControllerCallingServiceIsFlaggedUnderDefaultOrder` records this inversion; it is not a working feature.
- AMP014 has two reachability limits, both tested through the snippet only:
  - The `Predict` branch matches an invocation named `Predict` in namespace `SignalGrid.Ampelum`. The real `Predict` API has no method of that name (`Predict.Create` and `Predict<TSig>` do not match), so the test declares a stand-in method in that namespace.
  - The opt-out attribute `SignalGrid.Ampelum.Analyzers.AllowBareLlmPromptsAttribute` is looked up by name, and no Ampelum assembly defines it. The test declares it inside the snippet. A consumer has to declare it the same way for the opt-out to work.
