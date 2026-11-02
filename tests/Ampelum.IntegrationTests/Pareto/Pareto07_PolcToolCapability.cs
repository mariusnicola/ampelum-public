#pragma warning disable AMPELUM_V09_PREVIEW
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum;
using Ampelum.IntegrationTests.Fixtures;
using SignalGrid.Ampelum.Safety.Tools;
using SignalGrid.Ampelum.Testing;
using SignalGrid.Ampelum.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.IntegrationTests.Pareto;

/// <summary>
/// Pareto integration test #7 (master plan Â§6.H, alpha.2). Policy-of-least-capability (POLC):
/// a tool registered with <see cref="ToolCategory.DataMutation"/> is denied at runtime by a
/// <see cref="ToolGatePolicy"/> whose <c>DenyCategories</c> contains the mutation category, while
/// a peer tool registered as <see cref="ToolCategory.DataAccess"/> (read-only) is admitted.
/// </summary>
/// <remarks>
/// <para>
/// "Compile-time" half: Roslyn analyzer <c>AMP002</c> (<c>Amp002ToolCategoryAnalyzer</c>) fires when an
/// <see cref="ITypedTool{T}"/> implementation lacks <see cref="ToolCategoryAttribute"/>. The compile-time
/// rule is "category MUST be declared", not "Read blocks Write" â€” that's a runtime policy decision
/// expressed via <see cref="ToolGatePolicy"/>. The compile-time half is exercised end-to-end by the
/// <c>tests/SignalGrid.Ampelum.Analyzers.Tests/Amp002Tests.cs</c> suite (cross-referenced; not duplicated here so
/// IntegrationTests does not pull in <c>Microsoft.CodeAnalysis</c>).
/// </para>
/// <para>
/// Note: <see cref="ToolCategoryKind"/> (used by <see cref="ToolCategoryAttribute"/>) and
/// <see cref="ToolCategory"/> (used by <see cref="ToolGatePolicy"/>) are intentionally distinct enums â€”
/// the former is a compile-time hint for AMP002, the latter is the runtime gating taxonomy.
/// </para>
/// </remarks>
public sealed class Pareto07_PolcToolCapability : IClassFixture<ParetoTestFixture>
{
    readonly ParetoTestFixture _fx;

    /// <summary>xUnit ctor injection.</summary>
    public Pareto07_PolcToolCapability(ParetoTestFixture fx) => _fx = fx;

    /// <summary>DataAccess tool is admitted; DataMutation tool throws <see cref="ForbiddenToolException"/>.</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public async Task PolcPolicy_DenyDataMutation_ReadAdmitted_WriteRejected()
    {
        var ct = TestContext.Current.CancellationToken;

        var registry = new InMemoryToolRegistry();
        registry.Register(new ToolRegistration(
            Name: "lookup_capital", Version: "1.0", Category: ToolCategory.DataAccess));
        registry.Register(new ToolRegistration(
            Name: "delete_record",  Version: "1.0", Category: ToolCategory.DataMutation));

        var policy = new ToolGatePolicy(
            DenyCategories: new HashSet<ToolCategory> { ToolCategory.DataMutation });

        var gate = new ToolGateHook(registry, policy);

        // Read-only request: hook admits, no exception.
        var readCtx = new HookContext
        {
            ModuleName = "Predict",
            Inputs = new Dictionary<string, object?>(),
        };
        readCtx.Set(ToolGateHook.RequestedKey, new[] { ("lookup_capital", "1.0") }
            .AsEnumerable());
        await gate.OnBeforeAsync(readCtx, ct);

        // Mutation request: hook throws ForbiddenToolException.
        var writeCtx = new HookContext
        {
            ModuleName = "Predict",
            Inputs = new Dictionary<string, object?>(),
        };
        writeCtx.Set(ToolGateHook.RequestedKey, new[] { ("delete_record", "1.0") }
            .AsEnumerable());
        var ex = await Should.ThrowAsync<ForbiddenToolException>(async () =>
            await gate.OnBeforeAsync(writeCtx, ct));
        ex.Message.ShouldContain("delete_record");
    }

    /// <summary>End-to-end: a Predict pipeline with ToolGateHook upstream of a request-emitting probe
    /// short-circuits before the LM call when the requested tool is denied.</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public async Task PolcPipeline_DeniedTool_AbortsBeforeLmCall()
    {
        var ct = TestContext.Current.CancellationToken;

        var registry = new InMemoryToolRegistry();
        registry.Register(new ToolRegistration(
            Name: "delete_record", Version: "1.0", Category: ToolCategory.DataMutation));

        var policy = new ToolGatePolicy(
            DenyCategories: new HashSet<ToolCategory> { ToolCategory.DataMutation });

        // RequestedKeyHook (Order=50) seeds the metadata that ToolGateHook (Order=200) inspects.
        var requestHook = new RequestedToolsHook(("delete_record", "1.0"));
        var gateHook = new ToolGateHook(registry, policy);

        using var fake = new FakeChatClient();
        // Enqueue an answer the LM SHOULD never produce â€” gate must abort first.
        fake.EnqueueText("[[ ## Answer ## ]]\nshould-not-arrive\n[[ ## completed ## ]]");

        var ctx = new AmpelumContext(
            fake,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance)
            { Hooks = new IHook[] { requestHook, gateHook } };

        var predict = new Predict<QaSig>(ctx);
        await Should.ThrowAsync<ForbiddenToolException>(async () =>
            await predict.ForwardAsync(new QaSig { Question = "Delete user 42." }, ct: ct));

        // The LM was never called â€” fake client still has the unconsumed enqueued response.
        fake.RecordedCalls.Count.ShouldBe(0);
    }

    /// <summary>Order=50 hook that seeds <see cref="ToolGateHook.RequestedKey"/> for a downstream gate.</summary>
    sealed class RequestedToolsHook : IHook
    {
        readonly (string Name, string Version)[] _requested;
        public RequestedToolsHook(params (string, string)[] requested) => _requested = requested;
        public int Order => 50;
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
        {
            ctx.Set(ToolGateHook.RequestedKey, _requested.AsEnumerable());
            return default;
        }
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) => default;
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
    }
}
