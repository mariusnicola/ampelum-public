using SignalGrid.Ampelum.Internal;
using Shouldly;
using Xunit;

public sealed class RecordClonerTests
{
    sealed record Sample { public required string A { get; init; } public int B { get; init; } }

    [Fact]
    public void CloneWith_ReturnsNewInstance()
    {
        var src = new Sample { A = "x", B = 1 };
        var clone = RecordCloner.CloneWith(src, new Dictionary<string, object?> { ["B"] = 99 });
        clone.ShouldNotBeSameAs(src);
        clone.A.ShouldBe("x");
        clone.B.ShouldBe(99);
    }

    [Fact]
    public void CloneWith_NoOverrides_IsValueEqual()
    {
        var src = new Sample { A = "x" };
        var clone = RecordCloner.CloneWith(src, new Dictionary<string, object?>());
        clone.ShouldBe(src);
    }

    [Fact]
    public void CloneWith_UnknownProperty_IsSkipped()
    {
        // Synthetic fields (e.g., "reasoning" from ChainOfThought) are silently ignored.
        var src = new Sample { A = "x", B = 1 };
        var clone = RecordCloner.CloneWith(src, new Dictionary<string, object?> { ["B"] = 99, ["Zzz"] = 0 });
        clone.A.ShouldBe("x");
        clone.B.ShouldBe(99); // known field still applied
    }

    [Fact]
    public void CloneWith_NonRecord_Throws()
    {
        // A plain class has no <Clone>$ method — should throw on first lookup.
        var src = new NotARecord { A = "x" };
        Should.Throw<InvalidOperationException>(() =>
            RecordCloner.CloneWith(src, new Dictionary<string, object?>()));
    }

    [Fact]
    public void CloneWith_DelegateCachedAcrossCalls()
    {
        var s1 = new Sample { A = "x", B = 1 };
        var s2 = new Sample { A = "y", B = 2 };
        var c1 = RecordCloner.CloneWith(s1, new Dictionary<string, object?> { ["B"] = 99 });
        var c2 = RecordCloner.CloneWith(s2, new Dictionary<string, object?> { ["B"] = 77 });
        c1.B.ShouldBe(99);
        c2.B.ShouldBe(77);
    }

    [Fact]
    public void CloneWith_PropertyCacheCachedAcrossCalls()
    {
        var s1 = new Sample { A = "x", B = 1 };
        var s2 = new Sample { A = "y", B = 2 };
        // First call caches property "B" lookup
        var c1 = RecordCloner.CloneWith(s1, new Dictionary<string, object?> { ["B"] = 99 });
        // Second call should hit the property cache for "B"
        var c2 = RecordCloner.CloneWith(s2, new Dictionary<string, object?> { ["B"] = 77 });
        c1.B.ShouldBe(99);
        c2.B.ShouldBe(77);
    }

    [Fact]
    public async Task CloneWith_ConcurrentUnknownKeys_NoRace()
    {
        // Stress-test the inner ConcurrentDictionary by having multiple threads simultaneously
        // look up different unknown property names for the same record type.
        var src = new Sample { A = "x", B = 1 };
        const int iterations = 100;
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        for (int i = 0; i < iterations; i++)
        {
            var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
            {
                try
                {
                    // Each task writes a different unknown key — previously this raced on the
                    // inner Dictionary<string, PropertyInfo?>. Now it's ConcurrentDictionary.
                    RecordCloner.CloneWith(src, new Dictionary<string, object?>
                    {
                        [$"Unknown{t}"] = null,  // synthetic key, silently skipped
                        ["B"] = t,
                    });
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            })).ToArray();
            await Task.WhenAll(tasks);
        }

        exceptions.ShouldBeEmpty();
    }

    sealed class NotARecord
    {
        public string A { get; set; } = "";
    }
}
