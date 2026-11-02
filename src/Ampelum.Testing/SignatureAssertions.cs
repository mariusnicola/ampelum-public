namespace SignalGrid.Ampelum.Testing;

/// <summary>
/// Framework-agnostic assertions over <see cref="ISignature"/> shapes.
/// Throws <see cref="SignatureAssertionException"/> on failure — works with any test framework.
/// </summary>
public static class SignatureAssertions
{
    /// <summary>Asserts that <typeparamref name="TSig"/> is a structurally valid Ampelum signature (record, has inputs and outputs).</summary>
    /// <typeparam name="TSig">Signature type to validate.</typeparam>
    public static void ShouldBeValidSignature<TSig>() where TSig : ISignature
    {
        _ = SignatureMetadataCache.For<TSig>();  // throws if invalid; returns if valid
    }

    /// <summary>Asserts that <typeparamref name="TSig"/> has an <see cref="InputAttribute"/>-annotated property named <paramref name="name"/>.</summary>
    /// <typeparam name="TSig">Signature type to inspect.</typeparam>
    /// <param name="name">Expected input field name.</param>
    public static void ShouldHaveInput<TSig>(string name) where TSig : ISignature
    {
        var meta = SignatureMetadataCache.For<TSig>();
        if (!meta.Inputs.Any(f => f.Name == name))
            throw new SignatureAssertionException(
                $"Signature '{typeof(TSig).Name}' has no [Input] field named '{name}'. Inputs: [{string.Join(", ", meta.Inputs.Select(f => f.Name))}]");
    }

    /// <summary>Asserts that <typeparamref name="TSig"/> has an <see cref="OutputAttribute"/>-annotated property named <paramref name="name"/>.</summary>
    /// <typeparam name="TSig">Signature type to inspect.</typeparam>
    /// <param name="name">Expected output field name.</param>
    public static void ShouldHaveOutput<TSig>(string name) where TSig : ISignature
    {
        var meta = SignatureMetadataCache.For<TSig>();
        if (!meta.Outputs.Any(f => f.Name == name))
            throw new SignatureAssertionException(
                $"Signature '{typeof(TSig).Name}' has no [Output] field named '{name}'. Outputs: [{string.Join(", ", meta.Outputs.Select(f => f.Name))}]");
    }
}

/// <summary>
/// Thrown by <see cref="SignatureAssertions"/> when a signature assertion fails.
/// The message describes which assertion failed and what the actual signature shape is.
/// </summary>
public sealed class SignatureAssertionException(string message) : Exception(message);
