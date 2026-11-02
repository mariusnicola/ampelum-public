using System.Text;
using SignalGrid.Ampelum.Evaluation;
using Shouldly;
using Xunit;

public sealed class CsvDatasetTests
{
    static Stream S(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Reads_Header_And_Rows()
    {
        var ds = new CsvDataset(() => S("a,b\n1,2\n3,4\n"));
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var r in ds.EnumerateAsync(TestContext.Current.CancellationToken)) rows.Add(r);
        rows.Count.ShouldBe(2);
        rows[0]["a"].ShouldBe("1");
        rows[1]["b"].ShouldBe("4");
    }

    [Fact]
    public async Task Empty_Stream_Zero_Rows()
    {
        var ds = new CsvDataset(() => S(""));
        var n = 0;
        await foreach (var _ in ds.EnumerateAsync(TestContext.Current.CancellationToken)) n++;
        n.ShouldBe(0);
        ds.RowCount.ShouldBe(0);
    }

    [Fact]
    public async Task Header_Only_Zero_Rows()
    {
        var ds = new CsvDataset(() => S("a,b\n"));
        var n = 0;
        await foreach (var _ in ds.EnumerateAsync(TestContext.Current.CancellationToken)) n++;
        n.ShouldBe(0);
    }

    [Fact]
    public async Task Quoted_Field_With_Comma()
    {
        var ds = new CsvDataset(() => S("a,b\n\"a,b\",c\n"));
        IReadOnlyDictionary<string, object?>? row = null;
        await foreach (var r in ds.EnumerateAsync(TestContext.Current.CancellationToken)) row = r;
        row!["a"].ShouldBe("a,b");
        row["b"].ShouldBe("c");
    }

    [Fact]
    public async Task Quoted_Field_With_Escaped_Quote()
    {
        var ds = new CsvDataset(() => S("a\n\"say \"\"hi\"\"\"\n"));
        IReadOnlyDictionary<string, object?>? row = null;
        await foreach (var r in ds.EnumerateAsync(TestContext.Current.CancellationToken)) row = r;
        row!["a"].ShouldBe("say \"hi\"");
    }

    [Fact]
    public async Task Missing_Trailing_Cells_Become_Null()
    {
        var ds = new CsvDataset(() => S("a,b,c\n1\n"));
        IReadOnlyDictionary<string, object?>? row = null;
        await foreach (var r in ds.EnumerateAsync(TestContext.Current.CancellationToken)) row = r;
        row!["a"].ShouldBe("1");
        row["b"].ShouldBeNull();
        row["c"].ShouldBeNull();
    }

    [Fact]
    public async Task Reiterable_Two_Enumerations_Independent()
    {
        var calls = 0;
        var ds = new CsvDataset(() => { calls++; return S("a\n1\n"); });
        await foreach (var _ in ds.EnumerateAsync(TestContext.Current.CancellationToken)) { }
        await foreach (var _ in ds.EnumerateAsync(TestContext.Current.CancellationToken)) { }
        calls.ShouldBe(2);
    }

    [Fact]
    public async Task RowCount_Set_After_Enumeration()
    {
        var ds = new CsvDataset(() => S("a\n1\n2\n"));
        ds.RowCount.ShouldBeNull();
        await foreach (var _ in ds.EnumerateAsync(TestContext.Current.CancellationToken)) { }
        ds.RowCount.ShouldBe(2);
    }

    [Fact]
    public async Task FromFile_Roundtrip()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "a\nx\n", TestContext.Current.CancellationToken);
            var ds = CsvDataset.FromFile(path);
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            await foreach (var r in ds.EnumerateAsync(TestContext.Current.CancellationToken)) rows.Add(r);
            rows.Single()["a"].ShouldBe("x");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FromFile_NullOrEmpty_Throws() =>
        Should.Throw<ArgumentException>(() => CsvDataset.FromFile(""));

    [Fact]
    public void Ctor_NullFactory_Throws() =>
        Should.Throw<ArgumentNullException>(() => new CsvDataset(null!));

    [Fact]
    public async Task Skips_Blank_Lines()
    {
        var ds = new CsvDataset(() => S("a\n1\n\n2\n"));
        var n = 0;
        await foreach (var _ in ds.EnumerateAsync(TestContext.Current.CancellationToken)) n++;
        n.ShouldBe(2);
    }

    [Theory]
    [InlineData("a,b,c", "a|b|c")]
    [InlineData("\"a,b\",c", "a,b|c")]
    [InlineData("\"a\"\"b\",c", "a\"b|c")]
    [InlineData("", "")]
    public void ParseLine_Cases(string raw, string pipe)
    {
        var parts = CsvDataset.ParseLine(raw);
        string.Join('|', parts).ShouldBe(pipe);
    }
}
