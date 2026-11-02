# Ampelum Enterprise Template

Starting point for regulated / production-grade workloads:

- `AddAmpelumAudit()` — redacted audit trail wired in `Program.cs`.
- `AddAmpelumBudget()` — token + request rate caps.
- AOT-ready (`PublishAot=true`, `IsTrimmable=true`, `InvariantGlobalization=true`).
- `docs/THREAT-MODEL.md` documents the trust boundaries.

## Quickstart

```bash
dotnet new ampelum-enterprise -n Acme.Enterprise
cd Acme.Enterprise
dotnet publish -c Release -r win-x64
```

Tune `Audit/AuditConfiguration.cs` and `Budget/BudgetConfiguration.cs`
for your environment.
