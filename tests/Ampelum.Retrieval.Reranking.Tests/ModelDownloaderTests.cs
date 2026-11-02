using System.Security.Cryptography;
using SignalGrid.Ampelum.Retrieval.Reranking;
using Shouldly;
using Xunit;

namespace Ampelum.Retrieval.Reranking.Tests;

public sealed class ModelDownloaderTests
{
    [Fact]
    public async Task ResolveAsync_AlreadyOnDisk_VerifiesHash_ReturnsPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ampelum-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "model.onnx");
        var bytes = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(file, bytes, TestContext.Current.CancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var sut = new ModelDownloader(downloader: _ => throw new InvalidOperationException("must not download"));
        try
        {
            var path = await sut.ResolveAsync(file, hash, TestContext.Current.CancellationToken);
            path.ShouldBe(file);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_HashMismatch_Throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ampelum-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "model.onnx");
        await File.WriteAllBytesAsync(file, [1], TestContext.Current.CancellationToken);
        var sut = new ModelDownloader(downloader: _ => throw new InvalidOperationException("must not download"));
        try
        {
            await Should.ThrowAsync<InvalidDataException>(() =>
                sut.ResolveAsync(file, "00", TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_NotOnDisk_DownloadsWritesAndVerifies()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ampelum-test-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "model.onnx");
        var bytes = new byte[] { 9, 8, 7 };
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var sut = new ModelDownloader(downloader: _ => Task.FromResult(bytes));
        try
        {
            var path = await sut.ResolveAsync(file, hash, TestContext.Current.CancellationToken);
            path.ShouldBe(file);
            File.Exists(file).ShouldBeTrue();
            (await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken)).ShouldBe(bytes);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResolveAsync_NotOnDisk_DownloadHashMismatch_Throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ampelum-test-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "model.onnx");
        var bytes = new byte[] { 9, 8, 7 };
        var sut = new ModelDownloader(downloader: _ => Task.FromResult(bytes));
        try
        {
            await Should.ThrowAsync<InvalidDataException>(() =>
                sut.ResolveAsync(file, "00", TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void DefaultConstructor_DoesNotThrow()
    {
        Should.NotThrow(() => new ModelDownloader());
    }
}
