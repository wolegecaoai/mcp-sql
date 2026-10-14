using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SqlServerMcp.Services;

namespace SqlServerMcp.Tools;

/// <summary>
/// 审计日志只读工具集：查询本服务自身产生的审计记录（写操作、守卫拒绝、执行失败）。
/// <para>
/// 安全设计：日志目录由服务端固定为 <c>&lt;配置目录&gt;/logs</c>，工具**不接受任何路径参数**，
/// 日期也只解析为 <c>yyyy-MM-dd</c> 后拼装固定文件名——避免审计查询变成任意文件读取入口。
/// 返回条数 Clamp 到 1–200，防止单次调用打爆上下文。
/// </para>
/// </summary>
[McpServerToolType]
public class AuditTools
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    /// <summary>合法的事件类型（与 AuditLogger 写入的事件名保持一致）。</summary>
    private static readonly string[] KnownEvents = { "write", "guard_rejected", "write_failed" };

    [McpServerTool(Name = "query_audit_log", ReadOnly = true, Idempotent = true)]
    [Description("查询本服务的审计日志（写操作提交/演练、守卫拒绝、执行失败）。可按 UTC 日期、数据库别名、事件类型、工具名过滤；默认查当天、最多返回 50 条。用于回溯「刚才那次写影响了多少行」「哪些语句被守卫拦下」。")]
    public static string QueryAuditLog(
        AuditLogger auditLogger,
        [Description("UTC 日期，格式 yyyy-MM-dd，默认今天")] string? date = null,
        [Description("数据库别名过滤，可选，如 PLC_DB")] string? database = null,
        [Description("事件类型过滤，可选：write（写操作）/ guard_rejected（守卫拒绝）/ write_failed（执行失败）")] string? eventType = null,
        [Description("工具名过滤，可选，如 execute_ddl、execute_query")] string? tool = null,
        [Description("最大返回条数，默认 50，上限 200（超出时保留最新的若干条）")] int limit = DefaultLimit)
    {
        var targetDate = ParseDate(date);
        ValidateEventType(eventType);

        var result = auditLogger.QueryEntries(
            targetDate,
            database,
            eventType,
            tool,
            Math.Clamp(limit, 1, MaxLimit));

        // 空态同样用信封：保持「输出恒为合法 JSON」的契约（提示放 notice，不要返回裸散文）
        if (!result.FileFound)
        {
            return EmptyResult(targetDate, result.SkippedLines,
                $"未找到 {targetDate:yyyy-MM-dd}（UTC）的审计日志文件，该日期可能尚未产生记录。");
        }

        if (result.Entries.Count == 0)
        {
            var skippedNote = result.SkippedLines > 0 ? $"另有 {result.SkippedLines} 行损坏记录已跳过。" : string.Empty;
            return EmptyResult(targetDate, result.SkippedLines,
                $"{targetDate:yyyy-MM-dd}（UTC）的审计日志中没有符合过滤条件的记录。{skippedNote}");
        }

        var payload = new
        {
            date = targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            count = result.Entries.Count,
            skipped_lines = result.SkippedLines,
            hint = "ts 为 UTC 时间；sql_sha256 可用于比对接同一语句的多次调用；mode=commit 表示已提交，dry-run 表示演练回滚。",
            entries = result.Entries.Select(ToWireFormat).ToList()
        };

        return JsonSerializer.Serialize(payload, DatabaseService.SerializerOptions);
    }

    /// <summary>空结果信封（含提示与脏行数），保证输出始终可被 JSON 解析。</summary>
    private static string EmptyResult(DateOnly date, int skippedLines, string notice)
    {
        var payload = new
        {
            date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            count = 0,
            skipped_lines = skippedLines,
            notice,
            entries = Array.Empty<object>()
        };

        return JsonSerializer.Serialize(payload, DatabaseService.SerializerOptions);
    }

    /// <summary>投影为输出结构：跳过空字段，减少模型上下文里的噪声。</summary>
    private static Dictionary<string, object?> ToWireFormat(AuditEntry entry)
    {
        var item = new Dictionary<string, object?>
        {
            ["ts"] = entry.Ts,
            ["event"] = entry.Event
        };

        AddIfPresent(item, "database", entry.Database);
        AddIfPresent(item, "provider", entry.Provider);
        AddIfPresent(item, "tool", entry.Tool);
        AddIfPresent(item, "mode", entry.Mode);
        AddIfPresent(item, "affected_rows", entry.AffectedRows);
        AddIfPresent(item, "reason", entry.Reason);
        AddIfPresent(item, "error", entry.Error);
        AddIfPresent(item, "sql_sha256", entry.SqlSha256);
        AddIfPresent(item, "sql_preview", entry.SqlPreview);

        return item;
    }

    private static void AddIfPresent(Dictionary<string, object?> item, string key, object? value)
    {
        if (value is null)
        {
            return;
        }

        if (value is string text && string.IsNullOrEmpty(text))
        {
            return;
        }

        item[key] = value;
    }

    /// <summary>解析日期参数；缺省为今天（UTC），格式错误时给出可操作的中文提示。</summary>
    private static DateOnly ParseDate(string? date)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return DateOnly.FromDateTime(DateTime.UtcNow);
        }

        if (DateOnly.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        throw new McpException(
            $"日期参数格式不正确：「{date}」。请使用 yyyy-MM-dd（UTC），例如 2026-10-14。");
    }

    /// <summary>校验事件类型，避免拼写错误被误读为「没有记录」。</summary>
    private static void ValidateEventType(string? eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return;
        }

        if (KnownEvents.Contains(eventType.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        throw new McpException(
            $"事件类型「{eventType}」无效。可用值：{string.Join("、", KnownEvents)}（留空表示不过滤）。");
    }
}
