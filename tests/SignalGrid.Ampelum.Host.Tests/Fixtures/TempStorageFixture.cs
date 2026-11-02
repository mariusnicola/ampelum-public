#pragma warning disable AMPELUM_V09_PREVIEW
using Microsoft.Extensions.Options;
using SignalGrid.Ampelum.Host.Persistence;

namespace SignalGrid.Ampelum.Host.Tests.Fixtures;

public sealed class TempStorageFixture : IDisposable
{
    public string TempDir { get; }
    public AmpelumHostOptions Options { get; }

    public TempStorageFixture()
    {
        TempDir = Path.Combine(Path.GetTempPath(), "ampelum-host-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempDir);
        Options = new AmpelumHostOptions
        {
            Storage = new AmpelumHostOptions.StorageOptions
            {
                SqlitePath = Path.Combine(TempDir, "host.db"),
                AuditFile = Path.Combine(TempDir, "audit.jsonl"),
                PromptDir = Path.Combine(TempDir, "prompts"),
            }
        };
    }

    public string SqlitePath => Options.Storage.SqlitePath;
    public string AuditFile => Options.Storage.AuditFile;
    public string PromptDir => Options.Storage.PromptDir;

    public void Dispose()
    {
        try { Directory.Delete(TempDir, true); } catch { }
    }
}

internal static partial class TestData
{
    public static HostSqliteConnectionFactory Factory(AmpelumHostOptions opts) =>
        new(Options.Create(opts));
}
