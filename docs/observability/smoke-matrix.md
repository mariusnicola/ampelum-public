# Ampelum Observability Smoke Matrix

## Mandatory tier (every PR)

| Backend | Image | Endpoint | Assertion |
|---|---|---|---|
| Jaeger | `jaegertracing/all-in-one:1.62.0` | `:16686` (UI), `:4318` (OTLP/HTTP) | OTLP span `service=ampelum-smoke` appears within 2s |
| Prometheus | `prom/prometheus:v2.55.1` | `:9090` | `ampelum_predict_total` metric appears within 2s |
| Application Insights | `mcr.microsoft.com/azure-monitor/applicationinsights/local:0.7.0` | `:8888` | TrackEvent appears in local emulator within 2s |

Boot via `docker compose up -d` from `tests/Ampelum.Observability.SmokeTests/`.
Compose file: `docker-compose.yml`. Prometheus scrape config: `prometheus.yml`.

Run the smoke gate with:

```bash
dotnet test tests/Ampelum.Observability.SmokeTests --filter Category=Docker
```

## Best-effort tier (env-gated, nightly)

LangSmith, LangFuse, and Phoenix smoke uses HTTP record/replay against
captured fixtures committed under each exporter's tests project — no live
cloud calls in CI. Optional nightly probes hit real cloud sandboxes when
the corresponding env vars are set:

- `AMPELUM_OBSERVABILITY_LANGSMITH_KEY` (+ `_PROJECT`)
- `AMPELUM_OBSERVABILITY_LANGFUSE_PUBLIC_KEY` (+ `_SECRET_KEY`)
- `AMPELUM_OBSERVABILITY_PHOENIX_ENDPOINT` (+ `_API_KEY`)

These probes live under `tests/Ampelum.ContractTests/Observability/` and
are gated `[Trait("Category","CloudIntegration")]`. Out of v0.9.0-alpha.5
scope to wire CI for them.

## Local Docker requirement

The Docker-gated tests (`[Trait("Category","Docker")]`) require Docker
Desktop on Windows or a working Docker daemon on Linux. Without Docker,
the suite skips the three backend assertions but the in-process fan-out
test (`ExporterFanOutSmokeTests`) still runs and proves the three
exporters chain correctly on the same `TraceRecord`.

## A/B comparison sink (forward-defined)

`tests/Ampelum.Observability.SmokeTests/IAbComparisonSink.cs` is a
forward-declared marker interface that v0.9d's `EvalComparisonReport`
will plug into. Once v0.9d ships, the smoke matrix asserts that A/B
reports surface in each wired observability backend.
