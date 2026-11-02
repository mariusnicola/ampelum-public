# AOT Publish

Ampelum can be published as a native AOT binary. The source generator emits
signature metadata for every signature. It emits property accessors only for
an assembly that carries `[assembly: GenerateSignatureMetadata]`. Some
reflection remains:
`Predict` reads input properties by reflection when the accessors were not
generated, and the record cloning step reflects over the signature type on
every call.

```bash
# Publish the AOT smoke project to a self-contained native binary.
dotnet publish tests/Ampelum.AotSmoke/Ampelum.AotSmoke.csproj -c Release -r linux-x64 -o ./_publish/aot-smoke

./_publish/aot-smoke/Ampelum.AotSmoke
# -> Predict completed: Paris
```

The smoke project drives a single `Predict<SmokeSig>.ForwardAsync` against
a scripted chat client, so it needs no key and no network. `PublishAot` is set
in its project file, and warnings are treated as errors so any AOT or trim
warning fails the publish. CI publishes and runs this same project as a
regression gate.
