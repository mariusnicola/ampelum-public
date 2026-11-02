using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace SignalGrid.Ampelum.Retrieval.Reranking;

/// <summary>Downloads + SHA-256-verifies a model file. The downloader function is injectable for tests.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class ModelDownloader
{
    private readonly Func<string, Task<byte[]>> _download;

    /// <summary>Creates a downloader. Pass a custom <paramref name="downloader"/> in tests.</summary>
    public ModelDownloader(Func<string, Task<byte[]>>? downloader = null)
    {
        _download = downloader ?? DefaultDownload;
    }

    /// <summary>Resolves a model file: returns its path if already on disk and hash matches; otherwise downloads, writes, verifies.</summary>
    public async Task<string> ResolveAsync(string targetPath, string expectedSha256Hex, CancellationToken ct)
    {
        if (File.Exists(targetPath))
        {
            await VerifyAsync(targetPath, expectedSha256Hex, ct).ConfigureAwait(false);
            return targetPath;
        }
        var url = $"https://huggingface.co/cross-encoder/ms-marco-MiniLM-L-6-v2/resolve/main/{Path.GetFileName(targetPath)}";
        var bytes = await _download(url).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllBytesAsync(targetPath, bytes, ct).ConfigureAwait(false);
        await VerifyAsync(targetPath, expectedSha256Hex, ct).ConfigureAwait(false);
        return targetPath;
    }

    private static async Task VerifyAsync(string path, string expectedHex, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var actual = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        if (!Convert.ToHexString(actual).Equals(expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SHA-256 mismatch for {path}");
        }
    }

    private static async Task<byte[]> DefaultDownload(string url)
    {
        using var http = new HttpClient();
        return await http.GetByteArrayAsync(url).ConfigureAwait(false);
    }
}
