# Ampelum Platform Template

Multi-tenant SaaS starting point with multi-provider routing.

## What's wired

- `TenantContext` — AsyncLocal carrier of the current tenant id.
- `TenantScopeMiddleware` — reads `X-Tenant-Id` and pushes onto `TenantContext`.
- `ProviderRouting` — two name-keyed `IChatClient` instances; the default
  resolves a provider based on `TenantContext.Current`.

## Quickstart

```bash
dotnet new ampelum-platform -n Acme.Platform
cd Acme.Platform
dotnet run
curl -H "X-Tenant-Id: acme" http://localhost:5000/whoami
```

## Roadmap

When v0.9g ships `AddAmpelumRouter()`, replace the manual keyed registration
in `Routing/ProviderRouting.cs` with the router.
