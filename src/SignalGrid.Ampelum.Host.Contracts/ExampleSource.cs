using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>
/// Discriminated transport abstraction for an optimizer training set. HQ-3 (spec §6/§17).
/// Three impls ship in v0.7: <see cref="InlineExamples"/>, <see cref="FileExamples"/>,
/// <see cref="RemoteExamples"/>. New impls (e.g. S3) add via subclass + new
/// <c>[JsonDerivedType]</c> attribute without breaking <see cref="OptimizeRequest"/>.
/// Wire discriminator: <c>"kind"</c> (per spec §6).
/// Note: spec names this <c>IExampleSource</c>; abstract record does not use I-prefix convention (Default A).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(InlineExamples), "inline")]
[JsonDerivedType(typeof(FileExamples),   "file")]
[JsonDerivedType(typeof(RemoteExamples), "remote")]
public abstract record ExampleSource;
