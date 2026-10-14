using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>ExportFormatter 导出格式化测试（CSV 转义 / Markdown 表格 / 空结果集表头）。</summary>
public class ExportFormatterTests
{
    private static QueryResult CreateResult(params (string Key, object? Value)[] cells)
    {
        var row = new Dictionary<string, object?>();
        foreach (var (key, value) in cells)
        {
            row[key] = value;
        }

        var names = row.Keys.ToList();
        return new QueryResult(new List<Dictionary<string, object?>> { row }, false, names.Count, names);
    }

    [Fact]
    public void ToCsv_普通值输出表头与数据()
    {
        var csv = ExportFormatter.ToCsv(CreateResult(("City", "北京"), ("Temp", 18.5)));
        Assert.Equal($"City,Temp{Environment.NewLine}北京,18.5", csv);
    }

    [Fact]
    public void ToCsv_含逗号引号换行的字段加引号并翻倍内部引号()
    {
        var csv = ExportFormatter.ToCsv(CreateResult(("Name", "a,b\"c\nd")));
        Assert.Equal($"Name{Environment.NewLine}\"a,b\"\"c\nd\"", csv);
    }

    [Fact]
    public void ToCsv_null输出空串()
    {
        var csv = ExportFormatter.ToCsv(CreateResult(("A", null), ("B", 1)));
        Assert.Equal($"A,B{Environment.NewLine},1", csv);
    }

    [Fact]
    public void ToMarkdown_竖线转义换行折叠()
    {
        var md = ExportFormatter.ToMarkdown(CreateResult(("Name", "a|b"), ("Note", "x\ny")));
        Assert.Equal(
            $"| Name | Note |{Environment.NewLine}| --- | --- |{Environment.NewLine}| a\\|b | x y |",
            md);
    }

    [Fact]
    public void ToJson_与QueryResult一致()
    {
        var result = CreateResult(("City", "北京"));
        Assert.Equal(result.ToJson(), ExportFormatter.ToJson(result));
    }

    [Fact]
    public void 空结果集_CSV与Markdown仍有表头()
    {
        var empty = new QueryResult(new List<Dictionary<string, object?>>(), false, 2, new[] { "A", "B" });
        Assert.Equal("A,B", ExportFormatter.ToCsv(empty));
        Assert.Equal($"| A | B |{Environment.NewLine}| --- | --- |", ExportFormatter.ToMarkdown(empty));
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+cmd")]
    [InlineData("-2+3+cmd")]
    [InlineData("@SUM(A1)")]
    [InlineData("\t=cmd")]
    [InlineData("\u00A0=cmd")]      // MI-6：NBSP 等 Unicode 空白不得绕过
    [InlineData("\u3000+cmd")]      // 全角空格
    [InlineData("\uFEFF=cmd")]      // BOM
    [InlineData("\u200B@SUM(A1)")]  // 零宽空格
    [InlineData("\u200C-cmd")]      // 零宽非连接符
    public void ToCsv_公式注入前缀加单引号转义(string payload)
    {
        // C1/MI-6：Excel/WPS 打开 CSV 时会执行以 = + - @ 开头的单元格（含不可见空白前置的绕过写法）
        var csv = ExportFormatter.ToCsv(CreateResult(("V", payload)));
        var dataLine = csv.Split(Environment.NewLine)[1];
        Assert.StartsWith("'", dataLine);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("-12.75")]
    [InlineData("-0.5")]
    public void ToCsv_合法负数不被误伤(string value)
    {
        var csv = ExportFormatter.ToCsv(CreateResult(("V", value)));
        Assert.Equal($"V{Environment.NewLine}{value}", csv);
    }

    [Fact]
    public void ToCsv_数字使用不变文化格式()
    {
        // 1234.5 在某些区域文化下会格式化为 1234,5（逗号）导致 CSV 断裂——不变文化必须是点
        var csv = ExportFormatter.ToCsv(CreateResult(("Value", 1234.5)));
        Assert.Contains("1234.5", csv);
        Assert.DoesNotContain("1234,5", csv);
    }
}
