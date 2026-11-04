using Shouldly;
using SignalGrid.Ampelum.Agent.CodeIntelligence;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class CodeIndexerCallTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ampelum-indexer-" + Guid.NewGuid().ToString("N")[..8]);

    public CodeIndexerCallTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    const string Source = """
        namespace Shop;

        public sealed class Pricing
        {
            public decimal ApplyDiscount(decimal amount, decimal percent)
            {
                return Round(amount - amount * percent / 100m);
            }

            private static decimal Round(decimal value)
            {
                return Math.Floor(value);
            }

            public IEnumerable<int> GetEnumerator2()
            {
                yield return Helper(1);
            }

            async Task<int> CountAsync() => await Task.FromResult(1);

            void Log()
            {
                if (Check(1)) { Console.WriteLine("x"); }
                var again = Build(2);
                foreach (var item in Items(3)) { }
            }

            public record Money(decimal Amount);
        }
        """;

    ExtractResult Index()
    {
        var file = Path.Combine(_dir, "Pricing.cs");
        File.WriteAllText(file, Source);
        return new CodeIndexer().Extract(file);
    }

    [Fact]
    public void MethodsWithPlainAndGenericReturnTypes_AreIndexedAsMethodsOnTheirOwnLine()
    {
        var result = Index();

        result.Error.ShouldBeNull();
        var methods = result.Symbols.Where(s => s.Kind == SymbolKind.Method).ToDictionary(s => s.Name);
        methods.Keys.Order().ShouldBe(["ApplyDiscount", "CountAsync", "GetEnumerator2", "Log", "Round"]);
        methods["ApplyDiscount"].LineStart.ShouldBe(5);
        methods["ApplyDiscount"].LineEnd.ShouldBe(8);
        methods["Round"].LineStart.ShouldBe(10);
    }

    [Fact]
    public void CallsInsideMethodBodies_BecomeCallEdges_AndStatementsAreNotTakenForMethods()
    {
        var result = Index();

        var calls = result.Edges!.Where(e => e.EdgeType == EdgeType.Call).Select(e => $"{e.SrcName}->{e.TgtName}").ToArray();
        calls.ShouldContain("ApplyDiscount->Round");
        calls.ShouldContain("Round->Floor");
        calls.ShouldContain("GetEnumerator2->Helper");
        calls.ShouldContain("Log->Check");
        calls.ShouldContain("Log->Build");
        // Statements that only look like declarations are no symbols.
        result.Symbols.Select(s => s.Name).ShouldNotContain("Helper");
        result.Symbols.Select(s => s.Name).ShouldNotContain("Build");
        result.Symbols.Select(s => s.Name).ShouldNotContain("Items");
        result.Symbols.Select(s => s.Name).ShouldNotContain("Check");
    }

    [Fact]
    public void ExpressionBodiedMethods_EndAtTheirOwnStatement_SoTheirEdgesHoldOnlyTheirOwnCalls()
    {
        const string source = """
            namespace Shop;

            public sealed class Calc
            {
                public int Gen() => Tuple(1);

                public int Arr(int x)
                    => Nul(x)
                        + Log(x);

                public int Block()
                {
                    return Local(2);
                }

                public int Ctor(int a) : base(b => b)
                {
                    return Other(a);
                }
            }
            """;
        var file = Path.Combine(_dir, "Calc.cs");
        File.WriteAllText(file, source);

        var result = new CodeIndexer().Extract(file);

        var methods = result.Symbols.Where(s => s.Kind == SymbolKind.Method).ToDictionary(s => s.Name);
        methods["Gen"].LineEnd.ShouldBe(5);
        methods["Arr"].LineEnd.ShouldBe(9);
        methods["Block"].LineEnd.ShouldBe(14);
        methods["Ctor"].LineEnd.ShouldBe(19); // "=>" inside the parentheses is a lambda, not a body
        var edges = result.Edges!.Where(e => e.EdgeType == EdgeType.Call).GroupBy(e => e.SrcName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.TgtName).Order().ToArray());
        edges["Gen"].ShouldBe(["Tuple"]);
        edges["Arr"].ShouldBe(["Log", "Nul"]);
        edges["Block"].ShouldBe(["Local"]);
        edges["Ctor"].ShouldBe(["Other"]);
    }

    [Fact]
    public void ARecordWithAPrimaryConstructor_StaysOneRecordSymbol()
    {
        var result = Index();

        var money = result.Symbols.Where(s => s.Name == "Money").ShouldHaveSingleItem();
        money.Kind.ShouldBe(SymbolKind.Record);
    }

    [Fact]
    public void ATypeThatKeepsItsKind_EvenWhenAMethodNameContainsAKindWord()
    {
        var result = Index();

        // GetEnumerator2 contains "enum", which must not make it an Enum.
        result.Symbols.Single(s => s.Name == "GetEnumerator2").Kind.ShouldBe(SymbolKind.Method);
        result.Symbols.Single(s => s.Name == "Pricing").Kind.ShouldBe(SymbolKind.Class);
    }
}
