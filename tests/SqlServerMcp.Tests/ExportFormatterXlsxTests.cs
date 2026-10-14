using System.IO.Compression;
using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// XLSX 导出测试（H1）：布尔单元格、无引号数值、XML 非法字符剔除、公式注入防护、列字母超过 26。
/// 直接解包 base64 的 OOXML 检查 sheet1.xml。
/// </summary>
public class ExportFormatterXlsxTests
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

    private static string SheetXml(QueryResult result)
    {
        var bytes = Convert.FromBase64String(ExportFormatter.ToXlsx(result));
        using var memory = new MemoryStream(bytes);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        var entry = archive.GetEntry("xl/worksheets/sheet1.xml");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void ToXlsx_布尔写为布尔单元格()
    {
        var xml = SheetXml(CreateResult(("Flag", true), ("N", 0)));
        Assert.Contains("t=\"b\"", xml);
        Assert.Contains("<v>1</v>", xml);
    }

    [Fact]
    public void ToXlsx_整数为无引号数值()
    {
        var xml = SheetXml(CreateResult(("N", 123)));
        Assert.Contains("<v>123</v>", xml);
        Assert.DoesNotContain("'123'", xml);
    }

    [Fact]
    public void ToXlsx_剔除XML非法控制字符()
    {
        var xml = SheetXml(CreateResult(("A", "ok\u000Bbad")));
        Assert.DoesNotContain("\u000B", xml);
        Assert.Contains("okbad", xml);
    }

    [Fact]
    public void ToXlsx_公式注入加前缀()
    {
        // 与 CSV 一致：开头为 = 的字符串前置 ' 转义为文本；写入 XML 时 ' 被转义为 &apos;
        var xml = SheetXml(CreateResult(("A", "=1+1")));
        Assert.Contains("&apos;=1+1", xml);
    }

    [Fact]
    public void ToXlsx_列字母超过26()
    {
        // 27 列：表头行第 27 列列字母应为 AA
        var row = new Dictionary<string, object?>();
        for (var i = 0; i < 27; i++)
        {
            row[$"C{i}"] = i;
        }

        var names = row.Keys.ToList();
        var result = new QueryResult(new List<Dictionary<string, object?>> { row }, false, names.Count, names);
        var xml = SheetXml(result);
        Assert.Contains("r=\"AA1\"", xml);
    }
}
