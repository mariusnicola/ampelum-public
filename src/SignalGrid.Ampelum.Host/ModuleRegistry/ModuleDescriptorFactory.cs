using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Host.Contracts;

namespace SignalGrid.Ampelum.Host.ModuleRegistry;

/// <summary>Builds <see cref="ModuleDescriptor"/> instances from signature type metadata.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class ModuleDescriptorFactory
{
    /// <summary>
    /// Creates a <see cref="ModuleDescriptor"/> for the given signature type by reflecting
    /// on <see cref="SignatureMetadataCache"/>.
    /// </summary>
    public static ModuleDescriptor Create<TSig>(string moduleName, bool supportsStreaming)
        where TSig : ISignature
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleName);
        var meta = SignatureMetadataCache.For<TSig>();
        return new ModuleDescriptor(
            Name: moduleName,
            SignatureType: typeof(TSig).FullName ?? typeof(TSig).Name,
            Description: meta.Instructions,
            Inputs: meta.Inputs
                .Select(f => new FieldDescriptor(
                    f.Name,
                    f.ClrType.FullName ?? f.ClrType.Name,
                    string.IsNullOrEmpty(f.Description) ? null : f.Description,
                    !f.IsOptional))
                .ToArray(),
            Outputs: meta.Outputs
                .Select(f => new FieldDescriptor(
                    f.Name,
                    f.ClrType.FullName ?? f.ClrType.Name,
                    string.IsNullOrEmpty(f.Description) ? null : f.Description,
                    !f.IsOptional))
                .ToArray(),
            SupportsStreaming: supportsStreaming);
    }
}
