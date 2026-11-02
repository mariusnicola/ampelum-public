using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SignalGrid.Ampelum.Host.Endpoints;
using SignalGrid.Ampelum.Host.ModuleRegistry;
using SignalGrid.Ampelum.Host.Optimize;
using SignalGrid.Ampelum.Host.Persistence;

namespace SignalGrid.Ampelum.Host;

/// <summary>Extension methods for configuring and mapping Ampelum Host services and endpoints.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class AmpelumHostBuilderExtensions
{
    /// <summary>
    /// Registers all Ampelum Host services into the DI container.
    /// Optionally applies programmatic configuration via <paramref name="configure"/>.
    /// </summary>
    public static IServiceCollection AddAmpelumHost(
        this IServiceCollection services,
        Action<AmpelumHostOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<AmpelumHostOptions>()
            .BindConfiguration(AmpelumHostOptions.SectionName)
            .Validate(o => o.Port is > 0 and <= 65_535, "Host:Port must be 1..65535")
            .Validate(o => o.Sse.KeepAliveSeconds > 0, "Host:Sse:KeepAliveSeconds must be > 0")
            .Validate(o => o.Sse.FirstTokenDeadlineMs > 0, "Host:Sse:FirstTokenDeadlineMs must be > 0")
            .Validate(o => o.Audit.ChainSchemaVersion is 1 or 2, "Host:Audit:ChainSchemaVersion must be 1 or 2")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Storage.AuditFile), "Host:Storage:AuditFile required")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Storage.PromptDir), "Host:Storage:PromptDir required")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Storage.SqlitePath), "Host:Storage:SqlitePath required")
            .ValidateOnStart();

        if (configure is not null)
            services.PostConfigure(configure);

        services.AddSingleton<IHostSqliteConnectionFactory, HostSqliteConnectionFactory>();
        services.AddSingleton<IAuditIndex, AuditIndex>();
        services.AddSingleton<IPromptVersionStore, PromptVersionStore>();
        services.AddSingleton<IOptimizerRunStore, OptimizerRunStore>();
        services.AddSingleton<IModuleRegistrySnapshotStore, ModuleRegistrySnapshotStore>();
        services.AddSingleton<IModuleRegistry, ModuleRegistry.ModuleRegistry>();
        services.AddSingleton<IIndexRebuilder, IndexRebuilder>();
        services.AddSingleton<IReadinessState, ReadinessState>();
        services.AddHttpClient();
        services.AddSingleton<IOptimizerRunner, Optimize.OptimizerRunner>();
        services.AddSingleton<IOptimizerProgressBus, OptimizerProgressBus>();
        services.AddSingleton<Replay.IAuditEntryLookup, Replay.AuditEntryLookup>();
        services.AddScoped<Replay.IAuditReplayService, Replay.AuditReplayService>();

        return services;
    }


    /// <summary>
    /// Registers a module with the given name and factory. Call this after building the
    /// <see cref="WebApplication"/> and before <c>MapAmpelumHostEndpoints</c>.
    /// </summary>
    public static WebApplication MapAmpelumModule<TSig>(
        this WebApplication app,
        string moduleName,
        Func<IServiceProvider, Predict<TSig>> factory,
        bool supportsStreaming = false)
        where TSig : ISignature
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrEmpty(moduleName);
        ArgumentNullException.ThrowIfNull(factory);
        var registry = app.Services.GetRequiredService<IModuleRegistry>();
        if (registry.IsFrozen) throw new InvalidOperationException(
            "MapAmpelumModule must run before MapAmpelumHostEndpoints (which freezes the registry).");
        var predict = factory(app.Services);
        var adapter = new PredictComposedAdapter<TSig>(predict);
        var desc = ModuleDescriptorFactory.Create<TSig>(moduleName, supportsStreaming);
        registry.Register(new ModuleSlot(moduleName, adapter, desc));
        return app;
    }

    /// <summary>
    /// Freezes the module registry and maps all Ampelum Host endpoints.
    /// </summary>
    public static WebApplication MapAmpelumHostEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var registry = app.Services.GetRequiredService<IModuleRegistry>();
        registry.Freeze();

        var snapshotStore = app.Services.GetRequiredService<IModuleRegistrySnapshotStore>();
        foreach (var name in registry.Names)
        {
            var slot = registry.Get(name);
            snapshotStore.UpsertAsync(
                new ModuleSnapshotRow(name, slot.Descriptor.SignatureType, slot.SupportsStreaming, DateTimeOffset.UtcNow),
                default).GetAwaiter().GetResult();
        }

        var opts = app.Services.GetRequiredService<IOptions<AmpelumHostOptions>>().Value;
        if (registry.Count > 0 && string.IsNullOrEmpty(opts.OpenRouter.ApiKey))
            throw new InvalidOperationException(
                "Host:OpenRouter:ApiKey is required when at least one module is registered.");

        app.MapForwardEndpoint();
        app.MapStreamEndpoint();
        app.MapModulesEndpoints();
        app.MapAuditEndpoints();
        app.MapAuditReplayEndpoints();
        app.MapPromptsEndpoints();
        app.MapOptimizeEndpoints();
        app.MapHealthEndpoints();
        app.MapAdminEndpoints();
        app.MapOpenApiEndpoint();
        return app;
    }
}
