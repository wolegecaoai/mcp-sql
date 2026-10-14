using System.Globalization;
using System.Text;
// NumberStyles 需要 System.Globalization（已引入）

namespace SqlServerMcp.Services;

/// <summary>
/// 查询结果导出格式化（纯函数、零依赖、可单测）：CSV / Markdown / JSON。
/// </summary>
public static class ExportFormatter
{
    /// <summary>导出为 CSV：含逗号/引号/换行的字段按 RFC 4180 加引号并翻倍内部引号；null 输出为空串。</summary>
    public static string ToCsv(QueryResult result)
    {
        var columns = GetColumnNames(result);
        var builder = new StringBuilder();

        builder.AppendLine(string.Join(',', columns.Select(EscapeCsvField)));
        foreach (var row in result.Rows)
        {
            builder.AppendLine(string.Join(',', columns.Select(
                column => EscapeCsvField(FormatValue(row.TryGetValue(column, out var value) ? value : null)))));
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>导出为 Markdown 表格：单元格内竖线转义、换行折叠为空格；null 输出为空串。</summary>
    public static string ToMarkdown(QueryResult result)
    {
        var columns = GetColumnNames(result);
        var builder = new StringBuilder();

        builder.AppendLine("| " + string.Join(" | ", columns.Select(EscapeMarkdownCell)) + " |");
        builder.AppendLine("| " + string.Join(" | ", columns.Select(_ => "---")) + " |");
        foreach (var row in result.Rows)
        {
            builder.AppendLine("| " + string.Join(" | ", columns.Select(
                column => EscapeMarkdownCell(FormatValue(row.TryGetValue(column, out var value) ? value : null)))) + " |");
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>导出为紧凑 JSON 数组（与 execute_query 返回结构一致）。</summary>
    public static string ToJson(QueryResult result) => result.ToJson();

    private static List<string> GetColumnNames(QueryResult result)
    {
        if (result.ColumnNames is { Count: > 0 })
        {
            return result.ColumnNames.ToList();
        }

        // 兜底：老构造路径未带列名时取首行键（插入序）
        return result.Rows.Count > 0 ? result.Rows[0].Keys.ToList() : new List<string>();
    }

    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "True" : "False",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static string EscapeCsvField(string field)
    {
        // 先做公式注入防护（Excel/WPS 打开即以公式执行），再做 RFC 4180 引号转义
        var sanitized = SanitizeFormulaInjection(field);
        return sanitized.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0
            ? sanitized
            : "\"" + sanitized.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// CSV 公式注入防护（C1）：单元格以 =、+、-、@ 开头（含前置制表符/回车绕过）时前置单引号，
    /// 避免 Excel/WPS/Sheets 打开导出文件时把数据当公式/DDE 执行。合法负数（如 -5）不误伤。
    /// </summary>
    private static string SanitizeFormulaInjection(string field)
    {
        // MI-6：前置空白需覆盖全部 Unicode 空白与零宽字符（\v \f NBSP U+3000 零宽空格/非连接符 BOM 行分隔符），
        // 否则 "\u00A0=cmd" 这类写法可绕过前缀检测
        var index = 0;
        while (index < field.Length && IsLeadingInvisible(field[index]))
        {
            index++;
        }

        if (index == field.Length)
        {
            return field;
        }

        if (field[index] is '=' or '+' or '-' or '@'
            && !double.TryParse(field[index..], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return "'" + field;
        }

        return field;
    }

    /// <summary>前缀检测需跳过的不可见字符：Unicode 空白 + 常见零宽/格式化字符。</summary>
    private static bool IsLeadingInvisible(char c) =>
        char.IsWhiteSpace(c)
        || c is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF' or '\u2028' or '\u2029';

    private static string EscapeMarkdownCell(string cell) =>
        cell.Replace("|", "\\|").Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
}
