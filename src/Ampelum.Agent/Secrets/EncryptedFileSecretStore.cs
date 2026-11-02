using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

[assembly: InternalsVisibleTo("Ampelum.Agent.Tests")]

namespace SignalGrid.Ampelum.Agent;

/// <summary>Persistent encrypted secret store backed by a JSON file.
/// Values are encrypted at rest with AES-256-GCM.
/// Thread-safe via SemaphoreSlim; atomic writes via .tmp + File.Move.</summary>
public sealed class EncryptedFileSecretStore : ISecretStore, IDisposable
{
    private readonly string _filePath;
    private readonly byte[] _key;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    /// <summary>Creates the store at the default path (~/.ampelum/secrets.json)
    /// with a key derived from machine identity.</summary>
    public EncryptedFileSecretStore()
        : this(GetDefaultFilePath(), DeriveKey())
    {
    }

    /// <summary>Creates the store at a specific path with a given key.
    /// Visible for testing.</summary>
    internal EncryptedFileSecretStore(string filePath, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length != 32)
            throw new ArgumentException("Key must be exactly 32 bytes (256 bits) for AES-256-GCM.",
                nameof(key));

        _filePath = filePath;
        _key = key;
    }

    private static string GetDefaultFilePath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(userProfile, ".ampelum");
        return Path.Combine(dir, "secrets.json");
    }

    private static byte[] DeriveKey()
    {
        var raw = $"{Environment.MachineName}{Dns.GetHostName()}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    }

    // ── ISecretStore ──────────────────────────────────────────────────

    public async Task StoreAsync(string alias, string value, string jti,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await LoadInternalAsync(ct).ConfigureAwait(false);

            if (!data.TryGetValue(jti, out var session))
            {
                session = new Dictionary<string, string>(StringComparer.Ordinal);
                data[jti] = session;
            }

            session[alias] = Encrypt(value);
            await SaveInternalAsync(data, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> ResolveAsync(string alias, string jti,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await LoadInternalAsync(ct).ConfigureAwait(false);

            if (data.TryGetValue(jti, out var session) &&
                session.TryGetValue(alias, out var encrypted))
            {
                return Decrypt(encrypted);
            }

            throw new SecretNotFoundException(alias, jti);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> ExistsAsync(string alias, string jti,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await LoadInternalAsync(ct).ConfigureAwait(false);

            return data.TryGetValue(jti, out var session) &&
                   session.ContainsKey(alias);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ListAliasesAsync(string jti,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await LoadInternalAsync(ct).ConfigureAwait(false);

            if (!data.TryGetValue(jti, out var session))
                return Array.Empty<string>();

            return new List<string>(session.Keys).AsReadOnly();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(string alias, string jti,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await LoadInternalAsync(ct).ConfigureAwait(false);

            if (!data.TryGetValue(jti, out var session) || !session.Remove(alias))
                throw new SecretNotFoundException(alias, jti);

            await SaveInternalAsync(data, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task PurgeSessionAsync(string jti, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await LoadInternalAsync(ct).ConfigureAwait(false);
            data.Remove(jti);
            await SaveInternalAsync(data, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Persistence ───────────────────────────────────────────────────

    private async Task<Dictionary<string, Dictionary<string, string>>> LoadInternalAsync(
        CancellationToken ct)
    {
        EnsureDirectoryExists();

        if (!File.Exists(_filePath))
            return new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        var json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        var result = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json);
        return result ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
    }

    private async Task SaveInternalAsync(
        Dictionary<string, Dictionary<string, string>> data,
        CancellationToken ct)
    {
        EnsureDirectoryExists();

        var json = JsonSerializer.Serialize(data);
        var tmpPath = _filePath + ".tmp";
        await File.WriteAllTextAsync(tmpPath, json, ct).ConfigureAwait(false);
        File.Move(tmpPath, _filePath, overwrite: true);
    }

    private void EnsureDirectoryExists()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    // ── AES-256-GCM ───────────────────────────────────────────────────

    private string Encrypt(string plaintext)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[12]; // 96-bit nonce, standard for GCM
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[16]; // 128-bit authentication tag

        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, default);
        }
        catch (PlatformNotSupportedException ex)
        {
            throw new NotSupportedException(
                "AES-256-GCM is not supported on this platform. " +
                "EncryptedFileSecretStore requires AesGcm support " +
                "(Windows 10+, Linux with OpenSSL 1.1+, macOS 10.15+).", ex);
        }

        // Layout: nonce (12) + tag (16) + ciphertext (variable)
        var combined = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(combined, 0);
        tag.CopyTo(combined, nonce.Length);
        ciphertext.CopyTo(combined, nonce.Length + tag.Length);

        return Convert.ToBase64String(combined);
    }

    private string Decrypt(string encoded)
    {
        var combined = Convert.FromBase64String(encoded);

        if (combined.Length < 12 + 16)
            throw new InvalidOperationException(
                "Encrypted data is corrupt — too short to contain nonce and tag.");

        var nonce = combined.AsSpan(0, 12);
        var tag = combined.AsSpan(12, 16);
        var ciphertext = combined.AsSpan(28); // 12 + 16 = 28

        var plaintextBytes = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintextBytes, default);
        }
        catch (PlatformNotSupportedException ex)
        {
            throw new NotSupportedException(
                "AES-256-GCM decryption is not supported on this platform.", ex);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new InvalidOperationException(
                "Encrypted data is corrupt or tampered — authentication tag mismatch.", ex);
        }

        return Encoding.UTF8.GetString(plaintextBytes);
    }

    // ── Dispose ───────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;

        _lock.Dispose();
        _disposed = true;
    }
}
