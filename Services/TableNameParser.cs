using System.Text;
using ModelContextProtocol;

namespace SqlServerMcp.Services;

/// <summary>
/// 引号标识符感知的表名解析（M10 修复）。
/// 支持 <c>schema.table</c> 裸写与引号包裹形式：<c>[my.table]</c>、<c>"my.table"</c>、<c>`my.table`</c>
/// （引号内的点属于标识符本身，不参与切分），以及混合形式 <c>dbo.[my.table]</c>。
/// 引号内引号字符翻倍视为字面量（如 <c>[a]]b]</c> 解析为 <c>a]b</c>）。
/// </summary>
public static class TableNameParser
{
    /// <summary>
    /// 解析表名为 (schema, table)。未显式指定 schema 时 Schema 返回 null，由调用方按方言填补默认值。
    /// </summary>
    /// <exception cref="McpException">表名为空、片段数不为 1 或 2、存在空片段或引号未闭合时抛出。</exception>
    public static (string? Schema, string Table) Parse(string table)
    {
        if (string.IsNullOrWhiteSpace(table))
        {
            throw new McpException("表名不能为空。");
        }

        var segments = new List<string>();
        var current = new StringBuilder();
        char? quoteChar = null;
        var input = table.Trim();
        var index = 0;

        while (index < input.Length)
        {
            var c = input[index];

            if (quoteChar is null)
            {
                if (c is '[' or '"' or '`')
                {
                    quoteChar = c;
                    index++;
                    continue;
                }

                if (c == '.')
                {
                    segments.Add(current.ToString().Trim());
                    current.Clear();
                    index++;
                    continue;
                }

                current.Append(c);
                index++;
                continue;
            }

            // 引号内部：配对闭合符（[ 的闭合符为 ]，其余为自身），翻倍视为转义字面量
            var closer = quoteChar == '[' ? ']' : quoteChar.Value;
            if (c == closer)
            {
                if (index + 1 < input.Length && input[index + 1] == closer)
                {
                    current.Append(c);
                    index += 2;
                    continue;
                }

                quoteChar = null;
                index++;

                // 闭合后只允许分隔符 '.'、空白或结束，防止 [a]b 被静默拼接成 "ab"（M2）
                if (index < input.Length && input[index] is not ('.' or ' ' or '\t' or '\r' or '\n'))
                {
                    throw new McpException(
                        $"表名格式不正确：{table}。引号标识符闭合后只能出现 \".\" 或空白。");
                }

                continue;
            }

            current.Append(c);
            index++;
        }

        if (quoteChar is not null)
        {
            throw new McpException(
                $"表名格式不正确：{table}。引号标识符未正确闭合，请检查 []、\"\" 或 `` 是否成对。");
        }

        segments.Add(current.ToString().Trim());

        if (segments.Count is < 1 or > 2 || segments.Any(string.IsNullOrEmpty))
        {
            throw new McpException(
                $"表名格式不正确：{table}。请使用 \"表名\" 或 \"schema.表名\"（含点的标识符请用引号包裹，如 [my.table]）。");
        }

        return segments.Count == 1
            ? (null, segments[0])
            : (segments[0], segments[1]);
    }
}
