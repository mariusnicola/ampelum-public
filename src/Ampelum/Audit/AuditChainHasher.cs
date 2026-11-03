using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SignalGrid.Ampelum.Audit;

/// <summary>
/// Deterministic SHA-256 hash helper for the audit chain. Spec Â§4.2; PSQ-1, PSQ-2.
///
/// <para><b>PII invariant (v0.9c):</b> when <see cref="Ampelum.Safety.Pii.PiiRedactionHook"/>
/// is installed, the inputs/outputs passed to <see cref="Compute"/> are the post-redaction
/// projections. Plaintext PII never reaches this method in that configuration. Hash chains
/// commit the redacted form by design; restoring plaintext invalidates the chain.
/// See <c>docs/security/threat-model-v1.md</c> Â§6.</para>
/// </summary>
internal static class AuditChainHasher
{
    /// <summary>32-byte zero array, defensively copied per call (PSQ-2).</summary>
    public static byte[] ZeroHash => new byte[32];

    public static byte[] Compute(
        long sequence,
        DateTimeOffset timestamp,
        string moduleName,
        string modelId,
        IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyDictionary<string, object?> outputs,
        long promptTokens,
        long completionTokens,
        byte[] prevHash)
    {
        if (prevHash.Length != 32)
            throw new ArgumentException("PrevHash must be 32 bytes.", nameof(prevHash));

        AuditPrimitiveValidator.Validate("Inputs", inputs);
        AuditPrimitiveValidator.Validate("Outputs", outputs);

        Span<byte> i64 = stackalloc byte[8];

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        BinaryPrimitives.WriteInt64BigEndian(i64, sequence);                       sha.AppendData(i64);
        BinaryPrimitives.WriteInt64BigEndian(i64, timestamp.Ticks);                sha.AppendData(i64);
        sha.AppendData(Encoding.UTF8.GetBytes(moduleName));
        sha.AppendData(Encoding.UTF8.GetBytes(modelId));
        sha.AppendData(Encoding.UTF8.GetBytes(SerializeCanonical(inputs)));
        sha.AppendData(Encoding.UTF8.GetBytes(SerializeCanonical(outputs)));
        BinaryPrimitives.WriteInt64BigEndian(i64, promptTokens);                   sha.AppendData(i64);
        BinaryPrimitives.WriteInt64BigEndian(i64, completionTokens);               sha.AppendData(i64);
        sha.AppendData(prevHash);
        return sha.GetHashAndReset();
    }

    /// <summary>
    /// v1.0-rc1.alpha.12: SchemaVersion=2 chain hash. Identical layout to
    /// <see cref="Compute"/> (v1) but with <paramref name="eventType"/> folded into
    /// the canonical bytes between <c>moduleName</c> and <c>modelId</c>. Restores
    /// tamper detection on <c>EventType</c> that alpha.11 traded away when
    /// <c>ReplayHash</c> moved off the entry. Spec Â§4.2; PSQ-1, PSQ-2.
    /// </summary>
    public static byte[] ComputeV2(
        long sequence,
        DateTimeOffset timestamp,
        string moduleName,
        string eventType,
        string modelId,
        IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyDictionary<string, object?> outputs,
        long promptTokens,
        long completionTokens,
        byte[] prevHash)
    {
        if (prevHash.Length != 32)
            throw new ArgumentException("PrevHash must be 32 bytes.", nameof(prevHash));

        AuditPrimitiveValidator.Validate("Inputs", inputs);
        AuditPrimitiveValidator.Validate("Outputs", outputs);

        Span<byte> i64 = stackalloc byte[8];

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        BinaryPrimitives.WriteInt64BigEndian(i64, sequence);                       sha.AppendData(i64);
        BinaryPrimitives.WriteInt64BigEndian(i64, timestamp.Ticks);                sha.AppendData(i64);
        sha.AppendData(Encoding.UTF8.GetBytes(moduleName));
        sha.AppendData(new byte[] { 0 });
        sha.AppendData(Encoding.UTF8.GetBytes(eventType));
        sha.AppendData(new byte[] { 0 });
        sha.AppendData(Encoding.UTF8.GetBytes(modelId));
        sha.AppendData(Encoding.UTF8.GetBytes(SerializeCanonical(inputs)));
        sha.AppendData(Encoding.UTF8.GetBytes(SerializeCanonical(outputs)));
        BinaryPrimitives.WriteInt64BigEndian(i64, promptTokens);                   sha.AppendData(i64);
        BinaryPrimitives.WriteInt64BigEndian(i64, completionTokens);               sha.AppendData(i64);
        sha.AppendData(prevHash);
        return sha.GetHashAndReset();
    }

    /// <summary>
    /// v1.0-rc1.alpha.13: SchemaVersion=3 chain hash. Identical layout to
    /// <see cref="ComputeV2"/> (v2) but with <paramref name="traceId"/> appended
    /// after <c>completionTokens</c> and before <c>prevHash</c>, separated by a NUL
    /// sentinel byte. Folds the W3C TraceContext trace-id into the forward-integrity
    /// chain so trace-id tampering is detectable. Spec Â§4.2; PSQ-1, PSQ-2.
    /// </summary>
    public static byte[] ComputeV3(
        long sequence,
        DateTimeOffset timestamp,
        string moduleName,
        string eventType,
        string modelId,
        IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyDictionary<string, object?> outputs,
        long promptTokens,
        long completionTokens,
        string traceId,
        byte[] prevHash)
    {
        if (prevHash.Length != 32)
            throw new ArgumentException("PrevHash must be 32 bytes.", nameof(prevHash));

        AuditPrimitiveValidator.Validate("Inputs", inputs);
        AuditPrimitiveValidator.Validate("Outputs", outputs);

        Span<byte> i64 = stackalloc byte[8];

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        BinaryPrimitives.WriteInt64BigEndian(i64, sequence);                       sha.AppendData(i64);
        BinaryPrimitives.WriteInt64BigEndian(i64, timestamp.Ticks);                sha.AppendData(i64);
        sha.AppendData(Encoding.UTF8.GetBytes(moduleName));
        sha.AppendData(new byte[] { 0 });
        sha.AppendData(Encoding.UTF8.GetBytes(eventType));
        sha.AppendData(new byte[] { 0 });
        sha.AppendData(Encoding.UTF8.GetBytes(modelId));
        sha.AppendData(Encoding.UTF8.GetBytes(SerializeCanonical(inputs)));
        sha.AppendData(Encoding.UTF8.GetBytes(SerializeCanonical(outputs)));
        BinaryPrimitives.WriteInt64BigEndian(i64, promptTokens);                   sha.AppendData(i64);
        BinaryPrimitives.WriteInt64BigEndian(i64, completionTokens);               sha.AppendData(i64);
        sha.AppendData(new byte[] { 0 });
        sha.AppendData(Encoding.UTF8.GetBytes(traceId));
        sha.AppendData(prevHash);
        return sha.GetHashAndReset();
    }

    /// <summary>
    /// v1.0-rc1.alpha.10 (Gap 1): deterministic input-side hash that excludes
    /// <c>Outputs</c>, <c>PromptTokens</c>, <c>CompletionTokens</c>, <c>Sequence</c>,
    /// <c>PrevHash</c>, and <c>Timestamp</c>. Two calls with the same canonical
    /// inputs under the same module/event/model â€” even if one is streaming and the
    /// other is not â€” produce equal hashes. Returned as a lowercase hex string for
    /// direct equality assertions; the EntryHash variant returns raw bytes because
    /// it participates in the forward-integrity chain.
    /// </summary>
    public static string ComputeReplay(
        string moduleName,
        string eventType,
        string modelId,
        IReadOnlyDictionary<string, object?> inputs)
    {
        AuditPrimitiveValidator.Validate("Inputs", inputs);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(moduleName));
        sha.AppendData(new byte[] { 0 });
        sha.AppendData(Encoding.UTF8.GetBytes(eventType));
        sha.AppendData(new byte[] { 0 });
        sha.AppendData(Encoding.UTF8.GetBytes(modelId));
        sha.AppendData(new byte[] { 0 });
        sha.AppendData(Encoding.UTF8.GetBytes(SerializeCanonical(inputs)));
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    static string SerializeCanonical(IReadOnlyDictionary<string, object?> source) =>
        JsonSerializer.Serialize(
            AuditPrimitiveValidator.CanonicalMap("", source),
            typeof(SortedDictionary<string, object?>), AmpelumAuditJsonContext.Default);
}
