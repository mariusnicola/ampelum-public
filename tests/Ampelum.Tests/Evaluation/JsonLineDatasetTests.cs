using System.Text;
using SignalGrid.Ampelum.Evaluation;
using Shouldly;
using Xunit;

public sealed class JsonLineDatasetTests
{
    static Stream S(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Enumerates_All_Rows_From_Stream()
    {
        var ds = new JsonLineDataset(() => S(
            "{\"a\":1}\n{\"a\":2}\n{\"a\":3}\n"));
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var r in ds.EnumerateAsync(TestContext.Current.CancellationToken))
            rows.Add(r);
        rows.Count.ShouldBe(3);
        rows[0]["a"].ShouldBe(1L);
    }

    [Fact]
    public async Task Skips_Blank_Lines()
    {
        var ds = new JsonLineDataset(() => S("{\"a\":1}\n\n{\"a\":2}\n"));
        var n = 0;
        await foreach (var _ in ds.EnumerateAsync(TestContext.Current.CancellationToken)) n++;
        n.ShouldBe(2);
    }

    [Fact]
    public async Task Reiterable_Two_Enumerations_Independent()
    {
        var calls = 0;
        var ds = new JsonLineDataset(() => { calls++; return S("{\"a\":1}\n{\"a\":2}\n"); });
        await foreach (var _ in ds.EnumerateAsync(TestContext.Current.CancellationToken)) { }
        await foreach (var _ in ds.EnumerateAsync(TestContext.Current.CancellationToken)) { }
        calls.ShouldBe(2);
    }

    [Fact]
    public async Task RowCount_Set_After_Full_Enumeration()
    {
        var ds = new JsonLineDataset(() => S("{\"a\":1}\n{\"a\":2}\n"));
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
            await File.WriteAllTextAsync(path, "{\"a\":\"x\"}\n", TestContext.Current.CancellationToken);
            var ds = JsonLineDataset.FromFile(path);
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            await foreach (var r in ds.EnumerateAsync(TestContext.Current.CancellationToken)) rows.Add(r);
            rows.Single()["a"].ShouldBe("x");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FromFile_NullOrEmpty_Throws() =>
        Should.Throw<ArgumentException>(() => JsonLineDataset.FromFile(""));

    [Fact]
    public void Ctor_NullFactory_Throws() =>
        Should.Throw<ArgumentNullException>(() => new JsonLineDataset(null!));

    [Fact]
    public async Task Honors_CancellationToken()
    {
        var ds = new JsonLineDataset(() => S("{\"a\":1}\n{\"a\":2}\n"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in ds.EnumerateAsync(cts.Token)) { }
        });
    }

    [Fact]
    public async Task Parses_Mixed_Json_Value_Kinds()
    {
        var ds = new JsonLineDataset(() => S(
            "{\"s\":\"x\",\"i\":1,\"d\":1.5,\"t\":true,\"f\":false,\"n\":null,\"o\":{\"k\":1},\"a\":[1,2]}\n"));
        IReadOnlyDictionary<string, object?>? row = null;
        await foreach (var r in ds.EnumerateAsync(TestContext.Current.CancellationToken)) row = r;
        row!["s"].ShouldBe("x");
        row["i"].ShouldBe(1L);
        row["d"].ShouldBe(1.5);
        row["t"].ShouldBe(true);
        row["f"].ShouldBe(false);
        row["n"].ShouldBeNull();
        row["o"].ShouldBeOfType<string>();
        row["a"].ShouldBeOfType<string>();
    }
}
