using System.Text.Json;
using ModelContextProtocol;
using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// ResponseFormatter 测试：输出恒为合法 JSON、截断提示在信封内、三种形态与不支持语义。
/// </summary>
public sealed class ResponseFormatterTests
{
    private static QueryResult CreateResult(
        int rowCount = 2,
        bool truncated = false,
        bool truncatedBySize = false,
        int skipped = 0,
        string[]? columns = null)
    {
        columns ??= new[] { "id", "name" };
        var rows = Enumerable.Range(1, rowCount)
            .Select(i =>
            {
                var row = new Dictionary<string, object?>();
                for (var c = 0; c < columns.Length; c++)
                {
                    row[columns[c]] = c == 0 ? i : $"行{i}";
                }

                return row;
            })
            .ToList();

        return new QueryResult(rows, truncated, columns.Length, columns, skipped, truncatedBySize);
    }

    [Fact]
    public void 信封形态_是合法JSON且含元数据()
    {
        var text = ResponseFormatter.Format(CreateResult(), "PLC_DB", "execute_query", ResponseFormatter.Envelope, 100);

        using var document = JsonDocument.Parse(text);   // 旧实现在截断时会解析失败
        var root = document.RootElement;

        Assert.Equal("PLC_DB", root.GetProperty("database").GetString());
        Assert.Equal("execute_query", root.GetProperty("tool").GetString());
        Assert.Equal(2, root.GetProperty("row_count").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(new[] { "id", "name" }, root.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(2, root.GetProperty("rows").GetArrayLength());
        Assert.False(root.TryGetProperty("notice", out _));
        Assert.False(root.TryGetProperty("next", out _));
    }

    [Fact]
    public void 截断时提示在信封内且整体仍是合法JSON()
    {
        // 回归防线：旧实现会把「结果超过 N 行已截断…」追加在 JSON 数组之后，导致 JSON.parse 失败
        var text = ResponseFormatter.Format(
            CreateResult(rowCount: 3, truncated: true), "PLC_DB", "execute_query", ResponseFormatter.Envelope, 3);

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;

        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Contains("超过本次上限 3 行", root.GetProperty("notice").GetString());
        Assert.Equal(3, root.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public void 截断时给出续取offset()
    {
        var text = ResponseFormatter.Format(
            CreateResult(rowCount: 10, truncated: true, skipped: 20), "Db", "execute_query", ResponseFormatter.Envelope, 10);

        var next = JsonDocument.Parse(text).RootElement.GetProperty("next");

        Assert.Equal(30, next.GetProperty("offset").GetInt32());   // 已跳过 20 + 本次返回 10
        Assert.Contains("offset", next.GetProperty("hint").GetString());
    }

    [Fact]
    public void 按体积截断的提示文案与按行截断不同()
    {
        var text = ResponseFormatter.Format(
            CreateResult(rowCount: 2, truncated: true, truncatedBySize: true), "Db", "execute_query", ResponseFormatter.Envelope, 5000);

        var notice = JsonDocument.Parse(text).RootElement.GetProperty("notice").GetString();

        Assert.Contains("字符预算", notice);
        Assert.DoesNotContain("超过本次上限", notice);
    }

    [Fact]
    public void 紧凑形态_行是数组且与列顺序一致()
    {
        var text = ResponseFormatter.Format(CreateResult(rowCount: 2), "Db", "execute_query", ResponseFormatter.Compact, 100);

        var root = JsonDocument.Parse(text).RootElement;
        var rows = root.GetProperty("rows");

        Assert.Equal(2, rows.GetArrayLength());
        Assert.All(rows.EnumerateArray(), row => Assert.Equal(2, row.GetArrayLength()));
        Assert.Equal(new[] { "id", "name" }, root.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(1, rows[0][0].GetInt32());          // 顺序与 columns 对齐
        Assert.Equal("行1", rows[0][1].GetString());
    }

    [Fact]
    public void 紧凑形态_比信封形态更省字符()
    {
        var result = CreateResult(rowCount: 20, columns: new[] { "customer_id", "customer_name", "address" });

        var envelope = ResponseFormatter.Format(result, "Db", "execute_query", ResponseFormatter.Envelope, 100);
        var compact = ResponseFormatter.Format(result, "Db", "execute_query", ResponseFormatter.Compact, 100);

        Assert.True(compact.Length < envelope.Length,
            $"紧凑形态应更短：envelope={envelope.Length} compact={compact.Length}");
    }

    [Fact]
    public void 历史形态_返回裸数组且无元数据()
    {
        var text = ResponseFormatter.Format(CreateResult(), "Db", "execute_query", ResponseFormatter.Rows, 100);

        var root = JsonDocument.Parse(text).RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(2, root.GetArrayLength());
    }

    [Fact]
    public void 不支持时输出supported为false与原因()
    {
        var text = ResponseFormatter.Format(
            CreateResult(rowCount: 0, columns: Array.Empty<string>()),
            "SqliteExample",
            "list_stored_procedures",
            ResponseFormatter.Envelope,
            5000,
            supported: false,
            unsupportedReason: "SQLite 没有存储过程/函数概念，此查询不适用。");

        var root = JsonDocument.Parse(text).RootElement;

        Assert.False(root.GetProperty("supported").GetBoolean());
        Assert.Contains("没有存储过程", root.GetProperty("notice").GetString());
        Assert.Equal(0, root.GetProperty("row_count").GetInt32());
    }

    [Fact]
    public void 空结果仍带列信息()
    {
        var text = ResponseFormatter.Format(CreateResult(rowCount: 0, columns: new[] { "a", "b" }), "Db", "execute_query", ResponseFormatter.Envelope, 100);

        var root = JsonDocument.Parse(text).RootElement;

        Assert.Equal(0, root.GetProperty("row_count").GetInt32());
        Assert.Equal(new[] { "a", "b" }, root.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public void 跳过行数写入envelope的skipped字段()
    {
        var text = ResponseFormatter.Format(CreateResult(skipped: 7), "Db", "execute_query", ResponseFormatter.Envelope, 100);

        Assert.Equal(7, JsonDocument.Parse(text).RootElement.GetProperty("skipped").GetInt32());
    }

    [Theory]
    [InlineData("ENVELOPE", ResponseFormatter.Envelope)]
    [InlineData(" compact ", ResponseFormatter.Compact)]
    [InlineData(RowsConst, ResponseFormatter.Rows)]
    [InlineData(null, ResponseFormatter.Envelope)]
    [InlineData("", ResponseFormatter.Envelope)]
    public void 格式参数归一化(string? input, string expected)
    {
        Assert.Equal(expected, ResponseFormatter.NormalizeFormat(input));
    }

    private const string RowsConst = "rows";

    [Fact]
    public void 非法格式给出可用值()
    {
        var ex = Assert.Throws<McpException>(() => ResponseFormatter.NormalizeFormat("xml"));

        Assert.Contains("xml", ex.Message);
        Assert.Contains("compact", ex.Message);
        Assert.Contains("envelope", ex.Message);
    }

    [Fact]
    public void 目录类工具的截断提示不提maxRows()
    {
        // 目录工具没有 maxRows 参数，提示它去调该参数等于给出无法执行的建议（I2）
        var text = ResponseFormatter.Format(
            CreateResult(rowCount: 3, truncated: true), "Db", "list_tables", ResponseFormatter.Envelope, 10000,
            limitAdjustable: false);

        var notice = JsonDocument.Parse(text).RootElement.GetProperty("notice").GetString();

        Assert.DoesNotContain("maxRows", notice);
        Assert.Contains("offset", notice);
    }

    [Fact]
    public void offset越界时给出解释性提示()
    {
        var result = new QueryResult(
            new List<Dictionary<string, object?>>(), Truncated: false, ColumnCount: 1,
            ColumnNames: new[] { "x" }, SkippedRows: 100, TruncatedBySize: false, RequestedOffset: 150);

        var root = JsonDocument.Parse(
            ResponseFormatter.Format(result, "Db", "execute_query", ResponseFormatter.Envelope, 10)).RootElement;

        Assert.Equal(0, root.GetProperty("row_count").GetInt32());
        var notice = root.GetProperty("notice").GetString();
        Assert.Contains("超出结果集范围", notice);
        Assert.Contains("150", notice);
        Assert.Contains("100", notice);
    }

    [Fact]
    public void 列名缺失时从首行键推导()
    {
        var rows = new List<Dictionary<string, object?>> { new() { ["only"] = 1 } };
        var result = new QueryResult(rows, Truncated: false, ColumnCount: 1, ColumnNames: null);

        var root = JsonDocument.Parse(ResponseFormatter.Format(result, "Db", "t", ResponseFormatter.Envelope, 10)).RootElement;

        Assert.Equal(new[] { "only" }, root.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
    }
}
