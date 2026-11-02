namespace Company.Platform.Tenancy;

/// <summary>AsyncLocal carrier of the current tenant id across the request scope.</summary>
public static class TenantContext
{
    private static readonly AsyncLocal<string?> Current_ = new();

    public static string Current => Current_.Value ?? "default";

    public static IDisposable Enter(string tenantId)
    {
        var prior = Current_.Value;
        Current_.Value = tenantId;
        return new Restore(prior);
    }

    private sealed class Restore(string? prior) : IDisposable
    {
        public void Dispose() => Current_.Value = prior;
    }
}

/// <summary>Reads X-Tenant-Id from the request and pushes it into TenantContext.</summary>
public sealed class TenantScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext httpContext)
    {
        var tenantId = httpContext.Request.Headers["X-Tenant-Id"].FirstOrDefault() ?? "default";
        using var _ = TenantContext.Enter(tenantId);
        await next(httpContext).ConfigureAwait(false);
    }
}
