using System.Collections.Concurrent;
using System.Reflection;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Internal;

namespace SignalGrid.Ampelum;

/// <summary>
/// Process-lifetime cache for <see cref="SignatureMetadata"/>. Resolves metadata once per signature type
/// by reflecting on the record's properties and attributes, then caches the result for zero-allocation subsequent calls.
/// </summary>
public static class SignatureMetadataCache
{
    static readonly ConcurrentDictionary<Type, SignatureMetadata> _cache = new();
    static readonly ConcurrentDictionary<Type, Delegate[]> _accessors = new();

    /// <summary>
    /// Source-generator entry point for AOT-strict mode. Registers compile-time
    /// <see cref="Func{TSig, Object}"/> getter delegates for every <see cref="InputAttribute"/>
    /// field on <typeparamref name="TSig"/>, in the order the source generator emitted them
    /// (which matches <c>SignatureMetadata.Inputs</c> order). Calling this twice is a no-op:
    /// first-writer-wins via <see cref="ConcurrentDictionary{TKey, TValue}.TryAdd"/>.
    /// </summary>
    public static void RegisterAccessors<TSig>(Func<TSig, object?>[] getters) where TSig : ISignature
    {
        ArgumentNullException.ThrowIfNull(getters);
        var arr = new Delegate[getters.Length];
        for (var i = 0; i < getters.Length; i++) arr[i] = getters[i];
        _accessors.TryAdd(typeof(TSig), arr);
    }

    /// <summary>
    /// Returns delegate accessors registered by the AOT-strict source generator, or null.
    /// Used by <see cref="Predict{TSig}"/> to skip the reflective <c>PropertyInfo.GetValue</c>
    /// path when delegates are available.
    /// </summary>
    public static bool TryGetAccessors(Type signatureType, out Delegate[]? accessors)
    {
        if (_accessors.TryGetValue(signatureType, out var d))
        {
            accessors = d;
            return true;
        }
        accessors = null;
        return false;
    }

    /// <summary>Returns the <see cref="SignatureMetadata"/> for <typeparamref name="TSig"/>, building and caching it on first call.</summary>
    /// <typeparam name="TSig">The signature type to resolve. Must be a record with at least one <see cref="InputAttribute"/> and one <see cref="OutputAttribute"/>.</typeparam>
    /// <returns>Resolved and cached metadata for the signature.</returns>
    public static SignatureMetadata For<TSig>() where TSig : ISignature => For(typeof(TSig));

    /// <summary>Returns the <see cref="SignatureMetadata"/> for <paramref name="signatureType"/>, building and caching it on first call.</summary>
    /// <param name="signatureType">The signature type to resolve.</param>
    /// <returns>Resolved and cached metadata for the signature.</returns>
    public static SignatureMetadata For(Type signatureType) =>
        _cache.GetOrAdd(signatureType, Build);

    /// <summary>
    /// Source-generator entry point. Pre-populates metadata for a signature type, bypassing the
    /// reflection build. Calling <c>Register</c> after <c>For(type)</c> has already run for the
    /// same type is a no-op: the cache entry set by <c>For</c> takes precedence (first-writer-wins).
    /// </summary>
    /// <param name="signatureType">The signature type to pre-populate.</param>
    /// <param name="metadata">The pre-built metadata for the signature type.</param>
    /// <exception cref="ArgumentNullException"><paramref name="signatureType"/> or <paramref name="metadata"/> is null.</exception>
    public static void Register(Type signatureType, SignatureMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(signatureType);
        ArgumentNullException.ThrowIfNull(metadata);
        _cache.TryAdd(signatureType, metadata);
    }

    static SignatureMetadata Build(Type t)
    {
        if (!IsRecord(t))
            throw new SignatureDefinitionException(
                $"Signature type '{t.FullName}' must be a record in v0.1.");

        var instructions = t.GetCustomAttribute<SignatureAttribute>()?.Instructions ?? "";
        var inputs = new List<Field>();
        var outputs = new List<Field>();

        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var inAttr = p.GetCustomAttribute<InputAttribute>();
            var outAttr = p.GetCustomAttribute<OutputAttribute>();
            if (inAttr is null && outAttr is null) continue;
            if (inAttr is not null && outAttr is not null)
                throw new SignatureDefinitionException(
                    $"Property '{t.Name}.{p.Name}' is marked as both Input and Output.");

            if (!IsSupportedType(p.PropertyType))
                throw new SignatureDefinitionException(
                    $"Field '{t.Name}.{p.Name}' has unsupported type '{p.PropertyType}'.");

            var desc = inAttr?.Desc ?? outAttr?.Desc ?? HumanizeName.Split(p.Name);
            var kind = inAttr is not null ? FieldKind.Input : FieldKind.Output;
            var isOptional = NullabilityInspector.IsNullable(p);
            (kind == FieldKind.Input ? inputs : outputs)
                .Add(new Field(p.Name, p.PropertyType, desc, kind, isOptional));
        }

        if (inputs.Count == 0)
            throw new SignatureDefinitionException(
                $"Signature '{t.Name}' must have at least one [Input] field.");
        if (outputs.Count == 0)
            throw new SignatureDefinitionException(
                $"Signature '{t.Name}' must have at least one [Output] field.");

        return new SignatureMetadata(t, instructions, inputs, outputs);
    }

    static bool IsRecord(Type t) =>
        t.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is not null;

    static bool IsSupportedType(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (u == typeof(string)) return true;
        if (u.IsPrimitive) return true;
        if (u.IsEnum) return true;
        if (u == typeof(DateTime) || u == typeof(DateTimeOffset) || u == typeof(Guid)) return true;
        // v0.9a: System.Uri is a supported field type for multi-modal image input.
        if (u == typeof(Uri)) return true;
        if (u.IsArray) return IsSupportedType(u.GetElementType()!);
        if (u.IsGenericType)
        {
            var def = u.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(IReadOnlyList<>) || def == typeof(IList<>) || def == typeof(IEnumerable<>))
                return IsSupportedType(u.GetGenericArguments()[0]);
        }
        return false;
    }
}
