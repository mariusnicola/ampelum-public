# Ampelum documentation

This page is the single index for everything under `docs/`.

## Start here

- [Architecture](architecture.md): concepts, layers and how the packages relate
- [Configuration](configuration.md): keys, model selection, the identity signing key and Host settings. [.sample.env](../.sample.env) lists the model, provider and live-test variables; the other variables are under Other variables in that page
- [Use cases](../README.md#use-cases): the runnable samples, each with a `--fake` command
- [Known limits](../README.md#known-limits): what is partial or unfinished

## Learn by example

[Recipes](recipes/), in order of complexity:

| Getting started | Reliability and cost | Larger systems |
|---|---|---|
| [Hello Predict](recipes/01-hello-predict.md) | [Budget cap](recipes/04-budget-cap.md) | [Optimizer compile](recipes/09-optimizer-compile.md) |
| [Chain of thought](recipes/02-chain-of-thought.md) | [Audit chain validation](recipes/05-audit-chain-validation.md) | [Group chat handoff](recipes/13-group-chat-handoff.md) |
| [Streaming](recipes/03-streaming.md) | [Retry on parse failure](recipes/06-retry-on-parse-failure.md) | [AOT publish](recipes/14-aot-publish.md) |
| [ReAct with a tool](recipes/08-react-with-tool.md) | [Trace sink inspection](recipes/07-trace-sink-inspection.md) | [Evaluate harness](recipes/15-evaluate-harness.md) |
| [JSON adapter](recipes/20-json-adapter.md) | [Rate limit](recipes/10-rate-limit.md) | [Persistence round trip](recipes/16-persistence-roundtrip.md) |
| [Source generator](recipes/19-source-generator.md) | [Semantic cache](recipes/11-semantic-cache.md) | [RAG](recipes/17-rag.md) |
| | [Routed failover](recipes/12-routed-failover.md) | [Hook composition](recipes/18-hook-composition.md) |

## Coming from another framework

- [DSPy-py](migration/from-dspy-py.md)
- [LangChain](migration/from-langchain.md)
- [Raw Microsoft.Extensions.AI](migration/from-meai-raw.md)
- [Semantic Kernel](migration/from-semantic-kernel.md)

## Security

- [Threat model](security/threat-model-v1.md)
- [Audit replay hash](security/audit-replay-hash.md)

## Performance and quality

These pages are dated snapshots of measurements. Check the date and the platform in each one before relying on a number.

- [Comparative benchmark results](performance/comparative-results.md)
- [Semantic Kernel feature parity](performance/sk-feature-parity.md)
- [Mutation testing](performance/mutation-testing.md)
- [Observability smoke matrix](observability/smoke-matrix.md)

## Release engineering

- [Agent package graph](release/agent-package-graph.md)
- [Consumer smoke test](release/consumer-smoke.md)
- [NuGet dry run](release/nuget-dryrun.md)

## History

Release notes live in [CHANGELOG.md](../CHANGELOG.md). Current limits are listed under Known limits in the [root README](../README.md).
