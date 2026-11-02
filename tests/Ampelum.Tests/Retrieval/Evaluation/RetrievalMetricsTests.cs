using SignalGrid.Ampelum.Retrieval.Evaluation;
using Shouldly;
using Xunit;

public sealed class RecallAtKTests
{
    [Fact] public void Empty_Gold_Returns_One() =>
        new RecallAtK(5).Compute(new[] { "a" }, Array.Empty<string>()).ShouldBe(1.0);

    [Fact] public void All_Gold_In_TopK_Returns_One() =>
        new RecallAtK(3).Compute(new[] { "a", "b", "c" }, new[] { "a", "b" }).ShouldBe(1.0);

    [Fact] public void Half_Gold_In_TopK_Returns_Half() =>
        new RecallAtK(2).Compute(new[] { "a", "x" }, new[] { "a", "b" }).ShouldBe(0.5);

    [Fact] public void Default_K_Used_When_Override_Null() =>
        new RecallAtK(1).Compute(new[] { "a", "b" }, new[] { "a", "b" }).ShouldBe(0.5);

    [Fact] public void K_Override_Limits_TopK() =>
        new RecallAtK(10).Compute(new[] { "x", "a" }, new[] { "a" }, k: 1).ShouldBe(0.0);

    [Fact] public void Ctor_NonPositive_K_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new RecallAtK(0));

    [Fact] public void Name_Includes_K() => new RecallAtK(5).Name.ShouldBe("recall@5");

    [Fact]
    public void Null_Args_Throw()
    {
        var m = new RecallAtK(1);
        Should.Throw<ArgumentNullException>(() => m.Compute(null!, new[] { "a" }));
        Should.Throw<ArgumentNullException>(() => m.Compute(new[] { "a" }, null!));
    }
}

public sealed class PrecisionAtKTests
{
    [Fact] public void All_TopK_Are_Gold_Returns_One() =>
        new PrecisionAtK(2).Compute(new[] { "a", "b" }, new[] { "a", "b" }).ShouldBe(1.0);

    [Fact] public void Empty_Retrieved_Returns_Zero() =>
        new PrecisionAtK(5).Compute(Array.Empty<string>(), new[] { "a" }).ShouldBe(0.0);

    [Fact] public void Mixed_Hits_Computes_Fraction() =>
        new PrecisionAtK(2).Compute(new[] { "a", "x" }, new[] { "a" }).ShouldBe(0.5);

    [Fact] public void Default_K_Used_When_Override_Null() =>
        new PrecisionAtK(1).Compute(new[] { "a", "b" }, new[] { "a" }).ShouldBe(1.0);

    [Fact] public void K_Override_Limits() =>
        new PrecisionAtK(10).Compute(new[] { "a", "x" }, new[] { "a" }, k: 2).ShouldBe(0.5);

    [Fact] public void Name_Includes_K() => new PrecisionAtK(3).Name.ShouldBe("precision@3");

    [Fact] public void Null_Args_Throw()
    {
        var m = new PrecisionAtK(1);
        Should.Throw<ArgumentNullException>(() => m.Compute(null!, new[] { "a" }));
        Should.Throw<ArgumentNullException>(() => m.Compute(new[] { "a" }, null!));
    }

    [Fact] public void Ctor_NonPositive_K_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new PrecisionAtK(0));
}

public sealed class MRRTests
{
    [Fact] public void Empty_Gold_Returns_One() =>
        new MRR().Compute(new[] { "a" }, Array.Empty<string>()).ShouldBe(1.0);

    [Fact] public void First_Hit_Position_One_Returns_One() =>
        new MRR().Compute(new[] { "a", "b" }, new[] { "a" }).ShouldBe(1.0);

    [Fact] public void First_Hit_Position_Three_Returns_OneThird() =>
        new MRR().Compute(new[] { "x", "y", "a" }, new[] { "a" }).ShouldBe(1.0 / 3.0);

    [Fact] public void No_Hit_Returns_Zero() =>
        new MRR().Compute(new[] { "x", "y" }, new[] { "a" }).ShouldBe(0.0);

    [Fact] public void K_Limits_Search_Range() =>
        new MRR().Compute(new[] { "x", "y", "a" }, new[] { "a" }, k: 2).ShouldBe(0.0);

    [Fact] public void Name_Is_mrr() => new MRR().Name.ShouldBe("mrr");

    [Fact] public void Null_Args_Throw()
    {
        var m = new MRR();
        Should.Throw<ArgumentNullException>(() => m.Compute(null!, new[] { "a" }));
        Should.Throw<ArgumentNullException>(() => m.Compute(new[] { "a" }, null!));
    }
}

public sealed class NDCGAtKTests
{
    [Fact] public void Empty_Gold_Returns_One() =>
        new NDCGAtK(3).Compute(new[] { "a" }, Array.Empty<string>()).ShouldBe(1.0);

    [Fact] public void Perfect_Ranking_Returns_One() =>
        new NDCGAtK(3).Compute(new[] { "a", "b", "c" }, new[] { "a", "b", "c" }).ShouldBe(1.0, 1e-9);

    [Fact]
    public void Reversed_Ranking_Less_Than_One_Greater_Than_Zero()
    {
        var v = new NDCGAtK(3).Compute(new[] { "x", "y", "a" }, new[] { "a" });
        v.ShouldBeGreaterThan(0.0);
        v.ShouldBeLessThan(1.0);
    }

    [Fact] public void No_Hits_Returns_Zero() =>
        new NDCGAtK(3).Compute(new[] { "x", "y" }, new[] { "a" }).ShouldBe(0.0);

    [Fact] public void Default_K_Used_When_Override_Null() =>
        new NDCGAtK(1).Compute(new[] { "x", "a" }, new[] { "a" }).ShouldBe(0.0);

    [Fact] public void Name_Includes_K() => new NDCGAtK(5).Name.ShouldBe("ndcg@5");

    [Fact] public void Ctor_NonPositive_K_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new NDCGAtK(0));

    [Fact] public void Null_Args_Throw()
    {
        var m = new NDCGAtK(1);
        Should.Throw<ArgumentNullException>(() => m.Compute(null!, new[] { "a" }));
        Should.Throw<ArgumentNullException>(() => m.Compute(new[] { "a" }, null!));
    }
}
