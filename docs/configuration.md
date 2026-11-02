# Configuration

The library ships no keys and no model ids. `ModelConfig` and `ampelum repl` read environment variables only. The samples also read `.env` and `appsettings.json`. The Host reads `appsettings.json` and environment variables. The samples and `ampelum repl` hard-code the OpenRouter endpoint URL. An unset required model variable is an error that names the variable. The repository contains no `appsettings*.json` files.

## The sample file

[.sample.env](../.sample.env) in the repository root lists the model, provider and live-test variables and explains each one. The other variables the code reads are under Other variables below. The file holds no values. Copy it to `.env`, fill in what you need and keep `.env` out of git (it is ignored). Real environment variables override `.env`.

## Model settings

`ModelConfig` reads process environment variables only. It does not read the `.env` file itself, so export the variables or load the file with your shell or IDE before you start. The samples are different: they read `appsettings.json` and `.env` (searched upward from the program's folder) and then environment variables, with environment variables winning.

| Variable | Used for | When unset |
|---|---|---|
| `OPENROUTER_API_KEY` | OpenRouter key | `ModelConfig.OpenRouterApiKey` is null. Samples and `ampelum repl` fail with a message naming it. |
| `OPENROUTER_MODEL_1` | Model for normal calls (`ModelConfig.DefaultModel`) | `InvalidOperationException` naming the variable and `.sample.env` |
| `OPENROUTER_MODEL_2` | Model for tests (`ModelConfig.TestModel`) | Falls back to `OPENROUTER_MODEL_1`, then the same exception |
| `LMSTUDIO_URL` | LM Studio chat endpoint | `ModelConfig.LmStudioUrl` is null |
| `LMSTUDIO_MODEL` | LM Studio model | `InvalidOperationException` |
| `OLLAMA_URLin` | Ollama server URL | `ModelConfig.OllamaUrl` is null |
| `OLLAMA_MODEL` | Ollama model | `InvalidOperationException` |
| `AMPELUM_INTEGRATION_OPENROUTER_KEY` | Turns on the live provider tests | The tests that need it are skipped or not enabled |

The Ollama URL variable really is spelled `OLLAMA_URLin`. The code reads that exact name.

`ModelConfig.GetModel(provider)` accepts `openrouter`, `lmstudio` and `ollama` and returns the matching model, or throws for any other name.

## Model routing table

`ModelRoutingTable` in `Ampelum.Agent` maps task types to tiers (`Fast`, `Balanced`, `Capable`). It carries no model ids. You pass the tier configuration, each tier with a primary model, an optional fallback model and a provider name, to `ModelRoutingTable.WithDefaultRules(tiers)` or build the table yourself. `ConfiguredModelRouter` then picks a tier in this order: explicit tier on the task signal, matching task type rule, reasoning flag, interactive flag, fallback tier. Where the model ids come from is up to your application, for example from configuration.

## Agent identity signing key

`DefaultAgentIdentityProvider` takes the HMAC signing key as a required `byte[]` of at least 32 bytes. It throws `ArgumentException` for null or shorter keys, and there is no built-in key. Load the key from your own configuration or secret store. `LocalJwtIdentityProvider` needs no key because it generates an RSA key pair when it is created.

## Host settings

`SignalGrid.Ampelum.Host` binds the `Host` configuration section to `AmpelumHostOptions` with the standard ASP.NET Core configuration stack, so `appsettings.json` and environment variables both work. Environment variables use a double underscore in place of each colon. The options are validated at startup.

| Setting | Meaning | Value when unset |
|---|---|---|
| `Host:Port` | Only validated at startup (1 to 65535). Nothing else reads it. | `5170` |
| `Host:Url` | Not read by any code | `http://localhost:5170` |
| `Host:Storage:SqlitePath` | SQLite database file | `./data/host.db` |
| `Host:Storage:AuditFile` | JSONL audit log | `./data/audit.jsonl` |
| `Host:Storage:PromptDir` | Prompt version files | `./data/prompts` |
| `Host:OpenRouter:ApiKey` | OpenRouter key | empty. Startup fails if any module is registered and this is empty. |
| `Host:OpenRouter:DefaultModel` | Not read by any code | empty |
| `Host:Sse:KeepAliveSeconds` | Keep-alive interval for the stream and optimize endpoints, must be above 0 | `15` |
| `Host:Sse:FirstTokenDeadlineMs` | Only validated at startup (above 0). Nothing else reads it. | `1500` |
| `Host:Audit:ChainSchemaVersion` | Only validated at startup (1 or 2). Nothing else reads it. | `2` |
| `Host:AdminEndpointsEnabled` | Maps `/admin/*` | `false` |
| `Host:Modules:RegistryFile` | Reserved, not read | `./modules.json` |

The listening address is not set from these options. The Host does not read `OPENROUTER_API_KEY` or `ModelConfig`; `Host:OpenRouter:ApiKey` is separate:

```bash
export Host__OpenRouter__ApiKey=your-key
dotnet run --project src/SignalGrid.Ampelum.Host
```

The `rebuild-index` command reads `appsettings.json` from the working directory. Its `--config <path>` option is only recognized as the first argument, which is `rebuild-index` itself, so a custom path is not applied.

## Other variables

| Variable | Read by |
|---|---|
| `E2E_TRACE_LOG` | `E2ETrace`: path of the trace log (default is a file in the temp folder) |
| `E2E_CACHE_BACKEND` | `E2EFixture` in `Ampelum.Testing`: `memory` selects an in-memory cache, anything else a disk cache |
| `AMPELUM_E2E_SCENARIOS` | `ScenarioFixture` in `Ampelum.Testing`: `true` or `1` enables scenario tests |
| `AMPELUM_CONVENTIONS` | `ConventionManifest`: path to a conventions file, checked before the project and user files |
| `AMPL_SECRET_<alias>` | `EnvironmentSecretStore` in `Ampelum.Agent`: the value of the secret named `<alias>` |
| `AMPELUM_TEST_POSTGRES` | `PostgresWorkflowPersistenceTests` in `Ampelum.Agent.Tests`: connection string of an isolated database. The three tests fail when it is unset, and CI leaves them out |
