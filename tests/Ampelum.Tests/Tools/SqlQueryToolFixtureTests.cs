using SignalGrid.Ampelum.Tools;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Tools;

public enum SqlOperator { Eq, Gt, Lt, Like }

[ToolCategory(ToolCategoryKind.Read)]
public sealed class SqlQueryTool : ITypedTool<IReadOnlyList<IReadOnlyDictionary<string, object?>>>
{
    public string Name => "sql_query";
    public string Description => "Run a read-only SELECT query. Table + columns come from a typed allowlist.";

    public required string Table { get; init; }
    public required string[] Columns { get; init; }
    public string? FilterColumn { get; init; }
    public SqlOperator? FilterOp { get; init; }
    [FreeForm] public string? FilterValue { get; init; }
    public int Limit { get; init; } = 100;

    public ValueTask<IReadOnlyList<IReadOnlyDictionary<string, object?>>> InvokeAsync(CancellationToken ct)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
        [
            new Dictionary<string, object?> { ["id"] = 1L, ["name"] = "alice" },
            new Dictionary<string, object?> { ["id"] = 2L, ["name"] = "bob" }
        ];
        return ValueTask.FromResult(rows);
    }
}

public sealed class SqlQueryToolFixtureTests
{
    [Fact]
    public async Task Invoke_ReturnsRows()
    {
        var t = new SqlQueryTool { Table = "users", Columns = ["id", "name"], Limit = 10 };
        var rows = await t.InvokeAsync(TestContext.Current.CancellationToken);
        rows.Count.ShouldBe(2);
        rows[0]["name"].ShouldBe("alice");
    }

    [Fact]
    public void FilterValue_Property_HasFreeFormAttribute()
    {
        var prop = typeof(SqlQueryTool).GetProperty(nameof(SqlQueryTool.FilterValue))!;
        prop.GetCustomAttributes(typeof(FreeFormAttribute), false).Length.ShouldBe(1);
    }

    [Fact]
    public void Type_HasToolCategoryAttribute_Read()
    {
        var attr = typeof(SqlQueryTool).GetCustomAttributes(typeof(ToolCategoryAttribute), false)
            .Cast<ToolCategoryAttribute>().Single();
        attr.Kind.ShouldBe(ToolCategoryKind.Read);
    }
}
