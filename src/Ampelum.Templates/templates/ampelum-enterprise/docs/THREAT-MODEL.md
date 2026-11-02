# Threat Model — Enterprise Template

This template is the recommended starting point for regulated workloads.

## Trust boundaries

- Untrusted: end-user input, external `IChatClient` provider responses.
- Trusted: configuration, audit / budget options, host-process secrets.

## Mitigations wired by default

- **Audit:** every Predict invocation emits an audit record. Inputs and
  outputs are redacted by default (see `Audit/AuditConfiguration.cs`).
- **Budget:** per-day token cap + per-minute request cap on the global
  budget enforcer (see `Budget/BudgetConfiguration.cs`).
- **AOT:** the project publishes natively (`PublishAot=true`) reducing
  reflection-driven attack surface and producing a single deployable.

## Out-of-scope (you must add)

- Prompt-injection mitigations specific to your domain (allowlist, output
  validation against `ISignature` schema is provided; semantic checks are not).
- Network egress controls — Ampelum talks to any IChatClient implementation.
- Secret management — wire Azure Key Vault / AWS Secrets Manager / etc.

For the upstream threat model, see the Ampelum repo `docs/THREAT-MODEL.md`.
