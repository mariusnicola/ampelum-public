using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SignalGrid.Ampelum.DependencyInjection;

/// <summary>
/// DI extensions for the v0.6b audit subsystem (sink + hook).
/// </summary>
public static class AmpelumBuilderAuditExtensions
{
    /// <summary>Registers <see cref="JsonFileAuditSink"/> as the singleton <see cref="IAuditSink"/>.</summary>
    /// <param name="builder">Ampelum DI builder.</param>
    /// <param name="path">Filesystem path for the JSONL audit file.</param>
    public static AmpelumBuilder AddJsonFileAuditSink(this AmpelumBuilder builder, string path)
    {
        var services = builder.Services;
        services.RemoveAll<IAuditSink>();
        services.AddSingleton<IAuditSink>(_ => new JsonFileAuditSink(path));
        return builder;
    }

    /// <summary>Registers an <see cref="AuditHook"/> over the configured <see cref="IAuditSink"/> (default <see cref="InMemoryAuditSink"/>).</summary>
    /// <param name="builder">Ampelum DI builder.</param>
    /// <param name="replayMode">When true, the hook serves recorded outputs from the sink instead of recording.</param>
    public static AmpelumBuilder AddAuditHook(this AmpelumBuilder builder, bool replayMode = false)
    {
        var services = builder.Services;
        services.TryAddSingleton<IAuditSink, InMemoryAuditSink>();
        services.AddSingleton<IHook>(sp => new AuditHook(sp.GetRequiredService<IAuditSink>(), replayMode));
        return builder;
    }
}
