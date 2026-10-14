using System.Globalization;
using System.IO.Compression;
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

    /// <summary>
    /// 导出为 .xlsx 的 base64 字符串（自包含、无第三方依赖，手写最小 Office Open XML 包）。
    /// 调用方将返回值保存为 .xlsx 打开即可。字符串单元格采用内联字符串，并对公式注入字符做与 CSV 一致的防护。
    /// </summary>
    public static string ToXlsx(QueryResult result)
    {
        var columns = GetColumnNames(result);
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            AddZipEntry(archive, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "</Types>");

            AddZipEntry(archive, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");

            AddZipEntry(archive, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");

            AddZipEntry(archive, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "</Relationships>");

            var sheet = new StringBuilder();
            sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sheet.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            sheet.Append(BuildXlsxRow(1, columns.Select(c => (object?)c)));
            var rowIndex = 2;
            foreach (var row in result.Rows)
            {
                sheet.Append(BuildXlsxRow(rowIndex, columns.Select(c => row.TryGetValue(c, out var v) ? v : null)));
                rowIndex++;
            }

            sheet.Append("</sheetData></worksheet>");
            AddZipEntry(archive, "xl/worksheets/sheet1.xml", sheet.ToString());
        }

        return Convert.ToBase64String(memory.ToArray());
    }

    private static void AddZipEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string BuildXlsxRow(int rowIndex, IEnumerable<object?> values)
    {
        var builder = new StringBuilder();
        builder.Append("<row r=\"").Append(rowIndex).Append("\">");
        var column = 1;
        foreach (var value in values)
        {
            var reference = ColumnLetter(column) + rowIndex;
            if (value is null)
            {
                builder.Append("<c r=\"").Append(reference).Append("\"/>");
            }
            else if (value is bool flag)
            {
                // bool 用布尔单元格（t="b"），避免把 "True" 当数值写坏文件
                builder.Append("<c r=\"").Append(reference).Append("\" t=\"b\"><v>").Append(flag ? 1 : 0).Append("</v></c>");
            }
            else if (value is string text)
            {
                // 与 CSV 一致的公式注入防护：开头为 = + - @（含不可见前缀）时加 ' 转义为文本；并剔除 XML 非法字符
                var safe = SanitizeFormulaInjection(text);
                builder.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                    .Append(SafeXml(safe)).Append("</t></is></c>");
            }
            else if (TryNumericText(value, out var numeric))
            {
                // 仅真正的数值走 <v>（无引号、不变量格式）；NaN/Infinity 与非数值类型回落为字符串单元格
                builder.Append("<c r=\"").Append(reference).Append("\"><v>").Append(numeric).Append("</v></c>");
            }
            else
            {
                var asText = SanitizeFormulaInjection(value.ToString() ?? string.Empty);
                builder.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                    .Append(SafeXml(asText)).Append("</t></is></c>");
            }

            column++;
        }

        builder.Append("</row>");
        return builder.ToString();
    }

    /// <summary>仅当值为实数类型且可安全表示为不变体数字文本时返回 true（NaN/Infinity 除外，回落为字符串单元格）。</summary>
    private static bool TryNumericText(object value, out string text)
    {
        switch (value)
        {
            case sbyte v: text = v.ToString(CultureInfo.InvariantCulture); return true;
            case byte v: text = v.ToString(CultureInfo.InvariantCulture); return true;
            case short v: text = v.ToString(CultureInfo.InvariantCulture); return true;
            case ushort v: text = v.ToString(CultureInfo.InvariantCulture); return true;
            case int v: text = v.ToString(CultureInfo.InvariantCulture); return true;
            case uint v: text = v.ToString(CultureInfo.InvariantCulture); return true;
            case long v: text = v.ToString(CultureInfo.InvariantCulture); return true;
            case ulong v: text = v.ToString(CultureInfo.InvariantCulture); return true;
            case float v when !float.IsInfinity(v) && !float.IsNaN(v): text = v.ToString("R", CultureInfo.InvariantCulture); return true;
            case double v when !double.IsInfinity(v) && !double.IsNaN(v): text = v.ToString("R", CultureInfo.InvariantCulture); return true;
            case decimal v: text = v.ToString(CultureInfo.InvariantCulture); return true;
            default: text = string.Empty; return false;
        }
    }

    private static string ColumnLetter(int index)
    {
        var letter = string.Empty;
        while (index > 0)
        {
            var remainder = (index - 1) % 26;
            letter = (char)('A' + remainder) + letter;
            index = (index - 1) / 26;
        }

        return letter;
    }

    /// <summary>转义 XML 特殊字符，并剔除 XML 1.0 非法字符（控制字符、非字符、未配对代理项），避免 Excel 报“文件损坏”。</summary>
    private static string SafeXml(string text)
    {
        var cleaned = StripIllegalXmlChars(text);
        return cleaned
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");
    }

    /// <summary>
    /// 剔除 XML 1.0 不允许的字符：0x00–0x1F 中除 \t \n \r 外的控制字符、U+FFFE/U+FFFF，
    /// 以及未配对的 UTF-16 代理项（替换为 U+FFFD）。
    /// </summary>
    private static string StripIllegalXmlChars(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    builder.Append(c).Append(text[i + 1]);
                    i++;
                }
                else
                {
                    builder.Append('\uFFFD');
                }
            }
            else if (char.IsLowSurrogate(c))
            {
                builder.Append('\uFFFD');
            }
            else if (!IsIllegalXmlChar(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static bool IsIllegalXmlChar(char c)
    {
        if (c is '\t' or '\n' or '\r')
        {
            return false;
        }

        if (c < '\u0020')
        {
            return true;
        }

        return c is '\uFFFE' or '\uFFFF';
    }
}
