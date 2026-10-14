using System.Text.Json;
using ModelContextProtocol;

namespace SqlServerMcp.Services;

/// <summary>
/// 查询/目录类工具的统一输出格式。
/// <para>
/// **核心不变量：输出恒为合法 JSON。** 截断提示、分页提示、元数据全部放进信封字段——
/// 旧实现把「结果超过 N 行已截断…」这类中文散文直接追加在 JSON 数组之后，
/// 导致调用方 <c>JSON.parse</c> 必然失败（只能靠自己猜或重试）。
/// </para>
/// <para>
/// 三种形态：<see cref="Envelope"/>（默认，含元数据与提示）、
/// <see cref="Compact"/>（键名只出现一次，行以数组给出，宽表可省 40–60% token）、
/// <see cref="Rows"/>（历史裸数组，仅为兼容保留，不携带任何元数据）。
/// </para>
/// </summary>
public static class ResponseFormatter
{
    /// <summary>信封形态（默认）：元数据 + 提示 + 对象数组行。</summary>
    public const string Envelope = "envelope";

    /// <summary>紧凑形态：结构与信封一致，但行是「按 columns 顺序对齐的数组」。</summary>
    public const string Compact = "compact";

    /// <summary>历史形态：裸数组（无元数据、无提示），仅为兼容既有调用方保留。</summary>
    public const string Rows = "rows";

    /// <summary>受支持的响应格式（顺序即提示顺序）。</summary>
    public static readonly IReadOnlyList<string> SupportedFormats = new[] { Envelope, Compact, Rows };

    /// <summary>归一化并校验 response_format 参数；缺省为 <see cref="Envelope"/>。</summary>
    public static string NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return Envelope;
        }

        var normalized = format.Trim().ToLowerInvariant();
        if (SupportedFormats.Contains(normalized))
        {
            return normalized;
        }

        throw new McpException(
            $"不支持的响应格式「{format}」。可用值：{string.Join("、", SupportedFormats)}（默认 {Envelope}）。");
    }

    /// <summary>
    /// 格式化查询结果。
    /// </summary>
    /// <param name="result">查询结果（含截断与跳过元数据）。</param>
    /// <param name="database">数据库别名（信封字段，便于多轮对话中自我描述）。</param>
    /// <param name="tool">工具名。</param>
    /// <param name="responseFormat">输出形态，见 <see cref="SupportedFormats"/>。</param>
    /// <param name="limit">本次调用的行数上限（用于提示文案）。</param>
    /// <param name="supported">该数据库类型是否支持此查询；false 时输出 supported:false 与原因。</param>
    /// <param name="unsupportedReason">不支持的原因（supported 为 false 时使用）。</param>
    /// <param name="limitAdjustable">
    /// 该工具是否提供 maxRows 参数——目录类工具没有该参数，若提示「用 maxRows 提高」会给出无法执行的建议（I2）。
    /// </param>
    public static string Format(
        QueryResult result,
        string database,
        string tool,
        string? responseFormat,
        int limit,
        bool supported = true,
        string? unsupportedReason = null,
        bool limitAdjustable = true)
    {
        var format = NormalizeFormat(responseFormat);

        // 方言不支持该目录查询：必须用信封表达（裸数组无法携带 supported 语义）
        if (!supported)
        {
            return Serialize(new Dictionary<string, object?>
            {
                ["database"] = database,
                ["tool"] = tool,
                ["supported"] = false,
                ["notice"] = unsupportedReason ?? "该数据库类型不支持此查询。",
                ["row_count"] = 0,
                ["rows"] = Array.Empty<object>()
            });
        }

        var columns = ResolveColumns(result);
        var aligned = AlignRows(result, columns);

        // 历史形态：只给数据，不携带提示（需要提示请用 envelope）
        if (format == Rows)
        {
            return Serialize(aligned);
        }

        var payload = new Dictionary<string, object?>
        {
            ["database"] = database,
            ["tool"] = tool,
            ["columns"] = columns,
            ["row_count"] = aligned.Count,
            ["truncated"] = result.Truncated,
            ["rows"] = format == Compact ? ToArrays(columns, aligned) : aligned
        };

        if (result.SkippedRows > 0)
        {
            payload["skipped"] = result.SkippedRows;
        }

        var notice = BuildNotice(result, limit, aligned.Count, limitAdjustable);
        if (notice is not null)
        {
            payload["notice"] = notice;
        }

        if (result.Truncated)
        {
            payload["next"] = new Dictionary<string, object?>
            {
                ["offset"] = result.SkippedRows + aligned.Count,
                ["hint"] = "以相同 SQL 传入该 offset 可续取后续行；大表建议直接在 SQL 里 LIMIT/分页以减少扫描。"
            };
        }

        return Serialize(payload);
    }

    /// <summary>截断/越界提示（放信封内，不再是 JSON 之后的散文）。无需提示时返回 null。</summary>
    private static string? BuildNotice(QueryResult result, int limit, int returnedRows, bool limitAdjustable)
    {
        // offset 越界：既不是「没有数据」也不是「被截断」，必须显式说明，否则调用方无法区分（I3）
        if (returnedRows == 0 && result.RequestedOffset > result.SkippedRows)
        {
            return $"offset（{result.RequestedOffset}）已超出结果集范围：实际只有 {result.SkippedRows} 行。" +
                   "请改用更小的 offset（0 表示从头取）。";
        }

        if (!result.Truncated)
        {
            return null;
        }

        if (result.TruncatedBySize)
        {
            return $"结果因单次响应的字符预算被截断：本次返回 {returnedRows} 行（未达 {limit} 行上限）。" +
                   "请减少返回列、加上过滤条件缩小范围，或用 next.offset 续取。";
        }

        // 只有真正带 maxRows 参数的工具才提示调大它（目录类工具没有该参数）
        return limitAdjustable
            ? $"结果超过本次上限 {limit} 行已截断：本次返回 {returnedRows} 行。" +
              "可用 next.offset 续取，或用 maxRows 提高（上限 5000）、缩小查询范围。"
            : $"结果超过本次上限 {limit} 行已截断：本次返回 {returnedRows} 行。" +
              "可用 next.offset 续取，或缩小查询范围。";
    }

    /// <summary>列名：优先取结果集自带的列名，其次从首行键名推导（保证空结果也有列信息）。</summary>
    private static List<string> ResolveColumns(QueryResult result)
    {
        if (result.ColumnNames is { Count: > 0 })
        {
            return result.ColumnNames.ToList();
        }

        return result.Rows.Count > 0
            ? result.Rows[0].Keys.ToList()
            : new List<string>();
    }

    /// <summary>按列顺序对齐每行（保证 compact 形态与 columns 严格对应）。</summary>
    private static List<Dictionary<string, object?>> AlignRows(QueryResult result, List<string> columns)
    {
        if (columns.Count == 0)
        {
            return result.Rows;
        }

        return result.Rows
            .Select(row =>
            {
                var aligned = new Dictionary<string, object?>(columns.Count);
                foreach (var column in columns)
                {
                    aligned[column] = row.TryGetValue(column, out var value) ? value : null;
                }

                return aligned;
            })
            .ToList();
    }

    /// <summary>紧凑形态：键名只在 columns 出现一次，行是值数组。</summary>
    private static List<List<object?>> ToArrays(List<string> columns, List<Dictionary<string, object?>> rows) =>
        rows.Select(row => columns.Select(column => row.TryGetValue(column, out var value) ? value : null).ToList())
            .ToList();

    private static string Serialize(object payload) =>
        JsonSerializer.Serialize(payload, DatabaseService.SerializerOptions);
}
