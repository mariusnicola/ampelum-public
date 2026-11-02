using System.Collections.Generic;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Safety.Pii;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Safety.Pii;

public sealed class PiiAuditIntegrationTests
{
    [Fact]
    public async Task Audit_entry_hash_commits_redacted_inputs_when_redaction_hook_runs_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var inputs = new Dictionary<string, object?> { ["question"] = "ping alice@example.com" };

        // Pipeline A: with redaction.
        var sinkA = new InMemoryAuditSink();
        var auditA = new AuditHook(sinkA);
        var redaction = new PiiRedactionHook(new RegexPiiRedactor());
        var ctxA = new HookContext { ModuleName = "Predict", Inputs = inputs };
        await redaction.OnBeforeAsync(ctxA, ct);
        ctxA.Outputs = new Dictionary<string, object?> { ["answer"] = "ok" };
        await auditA.OnAfterAsync(ctxA, ct);

        // Pipeline B: no redaction.
        var sinkB = new InMemoryAuditSink();
        var auditB = new AuditHook(sinkB);
        var ctxB = new HookContext { ModuleName = "Predict", Inputs = inputs };
        ctxB.Outputs = new Dictionary<string, object?> { ["answer"] = "ok" };
        await auditB.OnAfterAsync(ctxB, ct);

        AuditEntry? entryA = null, entryB = null;
        await foreach (var e in sinkA.EnumerateAsync(ct)) entryA = e;
        await foreach (var e in sinkB.EnumerateAsync(ct)) entryB = e;

        entryA.ShouldNotBeNull();
        entryB.ShouldNotBeNull();
        entryA!.Inputs["question"].ShouldBe("ping <REDACTED:EMAIL>");
        entryB!.Inputs["question"].ShouldBe("ping alice@example.com");
        entryA.EntryHash.ShouldNotBe(entryB.EntryHash);
    }

    [Fact]
    public async Task Audit_falls_through_to_raw_inputs_when_redaction_metadata_absent()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = new InMemoryAuditSink();
        var audit = new AuditHook(sink);
        var ctx = new HookContext
        {
            ModuleName = "Predict",
            Inputs = new Dictionary<string, object?> { ["q"] = "raw" },
            Outputs = new Dictionary<string, object?> { ["a"] = "ok" },
        };
        await audit.OnAfterAsync(ctx, ct);
        AuditEntry? entry = null;
        await foreach (var e in sink.EnumerateAsync(ct)) entry = e;
        entry!.Inputs["q"].ShouldBe("raw");
    }
}
