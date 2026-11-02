using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Exceptions;
using Shouldly;
using Xunit;

public sealed class SignatureMetadataCacheTests
{
    [Signature("Answer questions.")]
    sealed record Qa : ISignature
    {
        [Input(Desc = "the question")] public required string Question { get; init; }
        [Output(Desc = "1-5 words")] public string Answer { get; init; } = "";
    }

    [Signature("")]
    sealed record NoInputs : ISignature
    {
        [Output] public string X { get; init; } = "";
    }

    [Signature("")]
    sealed record NoOutputs : ISignature
    {
        [Input] public required string X { get; init; }
    }

    [Signature("")]
    sealed record UnsupportedType : ISignature
    {
        [Input] public required Thread Rogue { get; init; } = null!;
        [Output] public string Y { get; init; } = "";
    }

    sealed class NotARecord : ISignature
    {
        [Input] public string X { get; set; } = "";
        [Output] public string Y { get; set; } = "";
    }

    [Signature("")]
    sealed record BothMarkers : ISignature
    {
        [Input, Output] public required string X { get; init; }
        [Output] public string Y { get; init; } = "";
    }

    [Fact]
    public void For_Record_ProducesMetadata()
    {
        var meta = SignatureMetadataCache.For<Qa>();
        meta.SignatureType.ShouldBe(typeof(Qa));
        meta.Instructions.ShouldBe("Answer questions.");
        meta.Inputs.Select(f => f.Name).ShouldBe(["Question"]);
        meta.Outputs.Select(f => f.Name).ShouldBe(["Answer"]);
        meta.Inputs[0].Description.ShouldBe("the question");
        meta.Inputs[0].Kind.ShouldBe(FieldKind.Input);
        meta.Outputs[0].Kind.ShouldBe(FieldKind.Output);
    }

    [Signature("")]
    sealed record Humanized : ISignature
    {
        [Input] public required string FirstName { get; init; }
        [Output] public string Greeting { get; init; } = "";
    }

    [Fact]
    public void For_HumanizesDescriptionWhenMissing()
    {
        var meta = SignatureMetadataCache.For<Humanized>();
        meta.Inputs[0].Description.ShouldBe("First Name");
    }

    [Fact]
    public void For_CachesByType()
    {
        var a = SignatureMetadataCache.For<Qa>();
        var b = SignatureMetadataCache.For<Qa>();
        ReferenceEquals(a, b).ShouldBeTrue();
    }

    [Fact]
    public void For_NotARecord_Throws() =>
        Should.Throw<SignatureDefinitionException>(SignatureMetadataCache.For<NotARecord>)
              .Message.ShouldContain("must be a record");

    [Fact]
    public void For_NoInputs_Throws() =>
        Should.Throw<SignatureDefinitionException>(SignatureMetadataCache.For<NoInputs>)
              .Message.ShouldContain("at least one");

    [Fact]
    public void For_NoOutputs_Throws() =>
        Should.Throw<SignatureDefinitionException>(SignatureMetadataCache.For<NoOutputs>);

    [Fact]
    public void For_UnsupportedFieldType_Throws() =>
        Should.Throw<SignatureDefinitionException>(SignatureMetadataCache.For<UnsupportedType>)
              .Message.ShouldContain(nameof(UnsupportedType.Rogue));

    [Fact]
    public void For_BothInputAndOutput_Throws() =>
        Should.Throw<SignatureDefinitionException>(SignatureMetadataCache.For<BothMarkers>)
              .Message.ShouldContain("both Input and Output");

    [Signature("")]
    sealed record Many : ISignature
    {
        [Input] public required string S { get; init; }
        [Input] public required int I { get; init; }
        [Input] public required double D { get; init; }
        [Input] public required bool B { get; init; }
        [Input] public required DateTime Dt { get; init; }
        [Input] public required DateTimeOffset Dto { get; init; }
        [Input] public required Guid G { get; init; }
        [Input] public required DayOfWeek E { get; init; }
        [Input] public required List<string> L { get; init; } = [];
        [Input] public required string[] Arr { get; init; } = [];
        [Input] public int? MaybeInt { get; init; }
        [Input] public string? MaybeStr { get; init; }
        [Output] public string Out { get; init; } = "";
    }

    [Fact]
    public void For_AllSupportedFieldTypes_Succeed()
    {
        var meta = SignatureMetadataCache.For<Many>();
        meta.Inputs.Count.ShouldBe(12);
    }

    sealed record NoAttr : ISignature
    {
        [Input] public required string X { get; init; }
        [Output] public string Y { get; init; } = "";
    }

    [Fact]
    public void For_MissingSignatureAttribute_UsesEmptyInstructions()
    {
        SignatureMetadataCache.For<NoAttr>().Instructions.ShouldBe("");
    }

    [Fact]
    public void For_ByType_IsEquivalentToGeneric()
    {
        var a = SignatureMetadataCache.For<Qa>();
        var b = SignatureMetadataCache.For(typeof(Qa));
        ReferenceEquals(a, b).ShouldBeTrue();
    }

    // Extra tests to cover IReadOnlyList<>, IList<>, IEnumerable<> branches in IsSupportedType
    [Signature("")]
    sealed record WithGenericCollections : ISignature
    {
        [Input] public required IReadOnlyList<int> ReadOnlyList { get; init; } = [];
        [Input] public required IList<int> List { get; init; } = new List<int>();
        [Input] public required IEnumerable<int> Enumerable { get; init; } = [];
        [Output] public string Out { get; init; } = "";
    }

    [Fact]
    public void For_IReadOnlyList_IList_IEnumerable_Supported()
    {
        var meta = SignatureMetadataCache.For<WithGenericCollections>();
        meta.Inputs.Count.ShouldBe(3);
        meta.Inputs.Select(f => f.Name).ShouldBe(["ReadOnlyList", "List", "Enumerable"]);
    }

    // Covers the generic-type-but-not-in-supported-list branch (line 72 in SignatureMetadataCache)
    [Signature("")]
    sealed record WithUnsupportedGeneric : ISignature
    {
        [Input] public required Dictionary<string, string> Map { get; init; } = [];
        [Output] public string Out { get; init; } = "";
    }

    [Fact]
    public void For_UnsupportedGenericType_Throws() =>
        Should.Throw<SignatureDefinitionException>(SignatureMetadataCache.For<WithUnsupportedGeneric>)
              .Message.ShouldContain(nameof(WithUnsupportedGeneric.Map));
}
