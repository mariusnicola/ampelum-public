namespace SignalGrid.Ampelum;

/// <summary>
/// Opt-in marker on a consuming assembly that switches the Ampelum source generator
/// into AOT-strict emission mode. When present, the generator emits delegate-based
/// property accessors registered via <c>SignatureMetadataCache.RegisterAccessors&lt;TSig&gt;</c>
/// in addition to the default <see cref="SignatureMetadataCache.Register(Type, SignatureMetadata)"/>
/// path. Default behavior (attribute absent) is unchanged.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class GenerateSignatureMetadataAttribute : Attribute
{
}
