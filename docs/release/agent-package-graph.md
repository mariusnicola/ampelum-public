# Private Agent package graph

The local/private output `_packs/` contains the generic Agent delivery artifacts
through `0.8.0-rc3`. Existing `0.8.0-rc1` and `0.8.0-rc2` artifacts are immutable and retained.

| Package | Direct package dependencies |
| --- | --- |
| `SignalGrid.Ampelum.Agent.Contracts` | none at runtime |
| `SignalGrid.Ampelum.Agent` | `Ampelum 0.8.0-rc1`, `SignalGrid.Ampelum.Agent.Contracts 0.8.0-rc2` |
| `SignalGrid.Ampelum.Agent.Postgres` | `SignalGrid.Ampelum.Agent 0.8.0-rc3`, `Npgsql 10.0.2` |
| `SignalGrid.Ampelum.Agent.Tools` | `SignalGrid.Ampelum.Agent 0.8.0-rc2` |
| `SignalGrid.Ampelum.Agent.Synthesis` | `Ampelum 0.8.0-rc1`, `SignalGrid.Ampelum.Agent 0.8.0-rc2` |

`tests/ConsumerSmoke.Tests` is the package-only consumer. Its `nuget.config`
uses `_packs/` and it has no `ProjectReference`; `DownstreamAgentPackageSmoke`
exercises a declarative lead-qualification workflow, mode-based tool policy,
versioned prompt compilation, the PostgreSQL provider assembly, and the Synthesis assembly bridge.

The explicit prerelease pins are intentional: the P5 graph moves together at
`0.8.0-rc2`, while its unchanged core dependency remains pinned at
`0.8.0-rc1`; downstream restore cannot accidentally select a stable sibling.

## Persistence boundary

`InMemoryWorkflowStateStore`, `InMemoryWorkflowRuntimeAuditSink`,
`InMemoryToolConfirmationStore`, and `InMemoryToolAuditLog` are process-local.
They lose state on process exit and must not be represented as production-durable.
For restart-safe observation tokens and tool-governance audit, use
`SqliteObservationTokenStore` and `SqliteToolAuditLog`; callers remain
responsible for database backup, encryption, retention, and multi-process
deployment policy. `SqliteWorkflowPersistence` is a durable provider for one
host and a locally locked SQLite file. `PostgresWorkflowPersistence` is the
production-selectable provider for multi-process PostgreSQL deployments. Inject
one persistence instance as both `IWorkflowStateStore` and
`IWorkflowRuntimeAuditSink`: the runtime then uses its atomic checkpoint plus
audit transaction. PostgreSQL operators retain ownership of TLS, roles,
backup/restore, retention, HA, and schema change control; SQLite files on
unreliable network shares are unsupported.
