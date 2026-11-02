# Ampelum Backend Template

ASP.NET Minimal API + Ampelum `Predict<TSig>` wired through DI.

## Quickstart

```bash
dotnet new ampelum-backend -n Acme.Api
cd Acme.Api
dotnet run
```

Then:

```bash
curl -X POST http://localhost:5000/ask -H "Content-Type: application/json" \
  -d '{"question":"What is the capital of France?"}'
```

## What's wired

- `AddAmpelum()` registers the runtime (default `IChatClient` from environment).
- `QASignature` declares one `[Input]` and one `[Output]`.
- `POST /ask` resolves `Predict<QASignature>` and runs it.

## Configuration

Provide an `IChatClient` via standard MEAI DI before `AddAmpelum()`. See the
Ampelum docs for OpenAI / OpenRouter / Anthropic wiring.
