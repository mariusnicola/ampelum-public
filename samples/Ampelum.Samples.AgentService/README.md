# Agent as a service

A program is served over HTTP by the Ampelum Host, a client calls it, a tool agent uses MCP tools, and the REPL and CLI are driven against the same answers.

`Program.cs` runs four parts in order:

1. **Host.** `AgentServiceHost.cs` serves one `Predict<AnswerSignature>` module with `AddAmpelumHost`, `MapAmpelumModule` and `MapAmpelumHostEndpoints`. `AgentServiceClient.cs` lists the modules, calls `POST /modules/Answer/forward`, reads `/audit` and `/audit/verify`, and probes `/readyz`.
2. **MCP tools.** `ToolAgent.cs` loads tools through `McpToolFactory.FromMcpSessionAsync` and hands them to a `ReAct` agent. A second path surveys tools through `McpToolSource` and an `IMcpTransport`.
3. **REPL.** `ReplSession.cs` feeds a script to `ReplLoop`, which evaluates it with Roslyn against the model.
4. **CLI.** `CliEval.cs` writes the service's answer to a JSONL file and runs `ampelum eval` on it as a process, with a 60 second limit after which the process is killed. This part runs only with `--fake`: the reference answer is the scripted one, so a live model has nothing valid to be scored against and live mode prints that the step is skipped.

## Run with a scripted model

```
dotnet run --project samples/Ampelum.Samples.AgentService/Ampelum.Samples.AgentService.csproj -c Release -- --fake
```

No key and no network beyond `127.0.0.1` are needed. The scripted answers live in `FakeScript.cs`. The exit code is 0 when the audit chain verifies, the REPL reports no error and the CLI exits 0.

## Run against a live model

Without `--fake` the sample reads `OPENROUTER_API_KEY` and `OPENROUTER_MODEL_1` from `.env` (copy `.sample.env` to `.env` and fill them in) or from environment variables. The same key is passed to the Host as `Host:OpenRouter:ApiKey`, which the Host requires whenever a module is mapped. The live path has not been run for this sample; only `--fake` and the tests have.

## Tests

```
dotnet test tests/Ampelum.Samples.AgentService.Tests/Ampelum.Samples.AgentService.Tests.csproj -c Release
```

The Host tests run on an in-memory `TestServer`, so they open no port. The CLI tests start the built CLI as a process, found from the .NET runtime directory rather than `PATH`.

## What does not work, and why

- **Streaming through the Host.** The module is mapped with streaming off, so `POST /modules/Answer/stream` answers 422 "Module does not support streaming." (tested). Turning streaming on does not work. Observed: a normal stream returns HTTP 200, then an `error` frame ("Stream failed: Cannot perform runtime binding on a null reference"), then `done`, with no `partial` and no `final` frame. When a hook short-circuits the model, one `partial` is sent, then an `error` frame ("'object' does not contain a definition for 'Trace'"), then `done`. Cause: `StreamEndpoint.cs` reads `chunk.Trace.Outputs` on chunks where `Predict.StreamAsync` sets `Trace` to null, and reads `Trace` from the stream object itself for `final`. So `final` can never be sent. The repo's Host tests read only the response headers of a stream, so they miss this. Streaming is available in process through `Predict.StreamAsync`.
- **Serving MCP tools.** `McpServerAdapter` answers `tools/list` and `tools/call` in process, but nothing in the library puts it behind a transport, so the sample consumes MCP tools and does not serve them. The tool source here (`OrderToolSource`) stands in for a connected MCP session; no MCP server runs.
- **Error mapping.** `ProblemDetailsMiddleware` is internal and the Host does not register it, so an unhandled exception inside an endpoint is not turned into one of the Host's error codes. The cases the endpoints handle themselves (unknown module 404, missing input 422, malformed body 400) are tested.
- **Optimize endpoints.** The Host scores optimization runs with a metric that always returns 1.0, so it is plumbing and not tuning. The sample leaves it out.
- **`ampelum repl`.** The CLI command builds a real chat client from the environment and refuses a fake. The REPL is shown through the library instead.
- **`ampelum eval`.** It scores predictions already in the dataset against the expected values. It calls no model, and `--signature` is only a label in the report.
- **Survey of discovered tools.** Without a manifest verifier, `McpToolSource` caps every tool a server lists to the most dangerous tier, whatever the server declares. The survey in this sample shows a tool declared safe that is governed as catastrophic.
