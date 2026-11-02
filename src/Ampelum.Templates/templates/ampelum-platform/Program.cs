using SignalGrid.Ampelum.DependencyInjection;
using Company.Platform.Routing;
using Company.Platform.Tenancy;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProviderRouting();
builder.Services.AddAmpelum();

var app = builder.Build();
app.UseMiddleware<TenantScopeMiddleware>();
app.MapGet("/whoami", () => new { tenant = TenantContext.Current });

app.Run();

public partial class Program;
