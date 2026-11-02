using SignalGrid.Ampelum;
using SignalGrid.Ampelum.DependencyInjection;
using Company.Enterprise.Audit;
using Company.Enterprise.Budget;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Enterprise template — wires Ampelum with Audit + Budget cross-cutting concerns
// for regulated workloads. Ships AOT-ready (PublishAot=true, IsTrimmable=true).

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAmpelum();
builder.Services.AddAmpelumAudit(AuditConfiguration.Configure);
builder.Services.AddAmpelumBudget(BudgetConfiguration.Configure);

var host = builder.Build();
await host.RunAsync();
