using SignalGrid.Ampelum.Context;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Context;

public class ProcessLockTests
{
    [Fact]
    public async Task Acquire_WritesPidAndTimestamp()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ampelum-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var l = await ProcessLock.AcquireAsync(dir, TestContext.Current.CancellationToken);
            // Read via FileShare.ReadWrite since ProcessLock holds the file with FileShare.Read
            using var fs = new FileStream(Path.Combine(dir, ".lock"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var contents = await sr.ReadToEndAsync(TestContext.Current.CancellationToken);
            contents.ShouldContain(Environment.ProcessId.ToString());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task DoubleAcquire_FailsWithLiveProcess()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ampelum-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var l1 = await ProcessLock.AcquireAsync(dir, TestContext.Current.CancellationToken);
            await Should.ThrowAsync<ProcessLockHeldException>(async () =>
                await ProcessLock.AcquireAsync(dir, TestContext.Current.CancellationToken));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Acquire_DeletesStaleLockFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ampelum-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // Write stale lock (PID 999999 likely doesn't exist)
            await File.WriteAllTextAsync(Path.Combine(dir, ".lock"),
                "999999\n2020-01-01T00:00:00Z\n", TestContext.Current.CancellationToken);
            await using var l = await ProcessLock.AcquireAsync(dir, TestContext.Current.CancellationToken);
            // After dispose the lock file should be gone
            if (File.Exists(Path.Combine(dir, ".lock")))
            {
                using var fs = new FileStream(Path.Combine(dir, ".lock"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                var contents = await sr.ReadToEndAsync(TestContext.Current.CancellationToken);
                contents.ShouldContain(Environment.ProcessId.ToString());
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
