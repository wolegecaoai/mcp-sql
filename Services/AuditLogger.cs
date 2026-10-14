using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SqlServerMcp.Services;

/// <summary>
/// 写操作审计日志（横切关注点）：写事件与守卫拒绝事件以 JSON 行追加落盘到
/// <c>&lt;配置目录&gt;/logs/audit-YYYYMMDD.log</c>。
/// 仅记录 SQL 的 SHA256 哈希与前 200 字符预览，不落完整语句，避免敏感字面量全量落盘。
/// 写文件失败不影响主流程（内部捕获后降级为 stderr 警告）。
/// </summary>
public sealed class AuditLogger
{
    private const int SqlPreviewLength = 200;

    private readonly string _logDirectory;
    private readonly ILogger<AuditLogger> _logger;

    /// <summary>并发写入闸门（File.AppendAllText 非线程安全）。</summary>
    private readonly object _gate = new();

    public AuditLogger(string logDirectory, ILogger<AuditLogger> logger)
    {
        _logDirectory = logDirectory;
        _logger = logger;
    }

    /// <summary>记录写操作事件（提交或演练回滚）。</summary>
    /// <param name="committed">true=已提交；false=演练模式已回滚。</param>
    public void LogWrite(string database, string providerId, string tool, bool committed, int affectedRows, string sql)
    {
        var entry = new Dictionary<string, object>
        {
            ["ts"] = Timestamp(),
            ["event"] = "write",
            ["database"] = database,
            ["provider"] = providerId,
            ["tool"] = tool,
            ["mode"] = committed ? "commit" : "dry-run",
            ["affected_rows"] = affectedRows
        };

        AppendEntry(entry, sql);
    }

    /// <summary>记录守卫拒绝事件（安全审计：黑名单/只读/单语句/分类等拦截）。</summary>
    public void LogGuardRejected(string database, string tool, string reason, string sql)
    {
        var entry = new Dictionary<string, object>
        {
            ["ts"] = Timestamp(),
            ["event"] = "guard_rejected",
            ["database"] = database,
            ["tool"] = tool,
            ["reason"] = reason
        };

        AppendEntry(entry, sql);
    }

    /// <summary>记录写操作执行失败事件（I4：连接/事务/执行阶段的异常）。</summary>
    /// <param name="tool">发起调用的工具名（MI-10：与其他事件保持一致，便于回溯）。</param>
    public void LogWriteFailed(string database, string providerId, string tool, string error, string sql)
    {
        var entry = new Dictionary<string, object>
        {
            ["ts"] = Timestamp(),
            ["event"] = "write_failed",
            ["database"] = database,
            ["provider"] = providerId,
            ["tool"] = tool,
            ["error"] = error
        };

        AppendEntry(entry, sql);
    }

    /// <summary>
    /// 查询指定 UTC 日期的审计条目（增量能力，不改动写入路径）。
    /// </summary>
    /// <param name="date">UTC 日期，对应文件名 audit-yyyyMMdd.log。</param>
    /// <param name="database">数据库别名过滤（大小写不敏感），null 表示不过滤。</param>
    /// <param name="eventType">事件类型过滤（write / guard_rejected / write_failed），null 表示不过滤。</param>
    /// <param name="tool">工具名过滤（大小写不敏感），null 表示不过滤。</param>
    /// <param name="limit">返回条数上限，超出时保留**最新**的 limit 条。</param>
    public AuditQueryResult QueryEntries(
        DateOnly date, string? database, string? eventType, string? tool, int limit)
    {
        var path = Path.Combine(_logDirectory, "logs", $"audit-{date:yyyyMMdd}.log");
        if (!File.Exists(path))
        {
            return new AuditQueryResult(Array.Empty<AuditEntry>(), 0, FileFound: false);
        }

        // 环形缓冲：流式逐行读取，只保留最新 limit 条，避免大文件整体载入内存
        var buffer = new Queue<AuditEntry>();
        var skipped = 0;

        try
        {
            // FileShare.ReadWrite：File.ReadLines 只共享读，查询期间并发的审计写入会因共享冲突失败，
            // 而 AppendEntry 会把这种失败降级为警告——等于悄悄丢审计记录（I2）
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var entry = TryParseEntry(line);
                if (entry is null)
                {
                    // 单行损坏（手工编辑/写入中断）不应导致整个查询失败
                    skipped++;
                    continue;
                }

                if (!Matches(entry, database, eventType, tool))
                {
                    continue;
                }

                buffer.Enqueue(entry);
                if (buffer.Count > limit)
                {
                    buffer.Dequeue();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "审计日志查询失败：{Path}", path);
        }

        return new AuditQueryResult(buffer.ToArray(), skipped, FileFound: true);
    }

    /// <summary>
    /// 清理超出保留期的**已完成日**审计文件（audit-YYYYMMDD.log）。
    /// 保留语义：保留最近 <paramref name="retentionDays"/> 天（含当日），更早的日文件删除。
    /// 安全边界：仅处理文件名严格匹配该模式的文件——当日文件与目录内其他任何文件永不删除；
    /// <paramref name="retentionDays"/> 为 0/负数表示不清理。
    /// </summary>
    /// <returns>实际删除的文件数（删除失败仅告警）。</returns>
    public int PruneOldFiles(int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        var directory = Path.Combine(_logDirectory, "logs");
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // 上限兜底（M3）：配置写成极大值时 today.AddDays 会越出 DateOnly 范围而抛异常
        var keepFrom = today.AddDays(-(Math.Min(retentionDays, 36500) - 1));
        var removed = 0;

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "audit-*.log"))
            {
                var fileDate = TryParseFileDate(Path.GetFileName(path));

                // 保留：无法识别日期的文件（不猜测）、保留期内文件、当日文件（显式兜底）
                if (fileDate is null || fileDate >= keepFrom || fileDate == today)
                {
                    continue;
                }

                // 至此 fileDate 必早于保留窗口起点 → 过期，删除

                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "审计日志清理失败（文件被占用或无权限）：{Path}", path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "审计日志清理失败（目录扫描）：{Directory}", directory);
        }

        if (removed > 0)
        {
            _logger.LogInformation("审计日志保留策略：已清理 {Count} 个过期文件（保留 {Days} 天）", removed, retentionDays);
        }

        return removed;
    }

    /// <summary>从 audit-YYYYMMDD.log 解析日期；不匹配则返回 null（绝不猜测要删除的文件）。</summary>
    private static DateOnly? TryParseFileDate(string fileName)
    {
        const string prefix = "audit-";
        const string suffix = ".log";

        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var middle = fileName[prefix.Length..^suffix.Length];
        return DateOnly.TryParseExact(middle, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static bool Matches(AuditEntry entry, string? database, string? eventType, string? tool)
    {
        if (!string.IsNullOrWhiteSpace(database)
            && !string.Equals(entry.Database, database.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(eventType)
            && !string.Equals(entry.Event, eventType.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(tool)
            && !string.Equals(entry.Tool, tool.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>解析单行 JSON；非对象或缺字段的脏行返回 null（由调用方计数跳过）。</summary>
    private static AuditEntry? TryParseEntry(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var root = document.RootElement;
            var entry = new AuditEntry
            {
                Ts = GetString(root, "ts") ?? string.Empty,
                Event = GetString(root, "event") ?? string.Empty,
                Database = GetString(root, "database"),
                Provider = GetString(root, "provider"),
                Tool = GetString(root, "tool"),
                Mode = GetString(root, "mode"),
                AffectedRows = GetInt64(root, "affected_rows"),
                Reason = GetString(root, "reason"),
                Error = GetString(root, "error"),
                SqlSha256 = GetString(root, "sql_sha256"),
                SqlPreview = GetString(root, "sql_preview")
            };

            return string.IsNullOrEmpty(entry.Event) ? null : entry;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    /// <summary>UTC ISO-8601 时间戳（M5：跨时区可排序、无本地时区歧义）。</summary>
    private static string Timestamp() =>
        DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private void AppendEntry(Dictionary<string, object> entry, string sql)
    {
        try
        {
            entry["sql_sha256"] = ComputeSha256(sql);
            entry["sql_preview"] = Truncate(sql, SqlPreviewLength);

            var line = JsonSerializer.Serialize(entry);
            var directory = Path.Combine(_logDirectory, "logs");
            Directory.CreateDirectory(directory);

            // 文件名与条目时间戳统一口径：均按 UTC 切日（MI-5：避免跨零点/跨时区错位）
            var path = Path.Combine(directory, $"audit-{DateTime.UtcNow:yyyyMMdd}.log");
            lock (_gate)
            {
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            // 审计写入失败（磁盘满/权限等）不得中断主流程
            _logger.LogWarning(ex, "审计日志写入失败（不影响主流程）");
        }
    }

    private static string ComputeSha256(string sql)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sql ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Truncate(string sql, int maxLength)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return string.Empty;
        }

        var text = sql.Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }
}

/// <summary>
/// 审计日志条目（查询结果投影）。字段与落盘 JSON 同名，便于使用者对照原始日志文件。
/// </summary>
public sealed class AuditEntry
{
    /// <summary>UTC ISO-8601 时间戳。</summary>
    public string Ts { get; init; } = string.Empty;

    /// <summary>事件类型：write / guard_rejected / write_failed。</summary>
    public string Event { get; init; } = string.Empty;

    /// <summary>数据库别名。</summary>
    public string? Database { get; init; }

    /// <summary>数据库类型标识（sqlserver / mysql / postgresql / sqlite）。</summary>
    public string? Provider { get; init; }

    /// <summary>发起调用的工具名。</summary>
    public string? Tool { get; init; }

    /// <summary>写事件模式：commit（已提交）/ dry-run（演练已回滚）。</summary>
    public string? Mode { get; init; }

    /// <summary>写事件的影响行数。</summary>
    public long? AffectedRows { get; init; }

    /// <summary>守卫拒绝原因。</summary>
    public string? Reason { get; init; }

    /// <summary>执行失败的错误信息。</summary>
    public string? Error { get; init; }

    /// <summary>SQL 的 SHA256 哈希（不落完整语句）。</summary>
    public string? SqlSha256 { get; init; }

    /// <summary>SQL 前 200 字符预览。</summary>
    public string? SqlPreview { get; init; }
}

/// <summary>审计查询结果。</summary>
/// <param name="Entries">命中的条目（按时间先后排列，最多 limit 条）。</param>
/// <param name="SkippedLines">跳过的损坏行数（非法 JSON 或非对象行）。</param>
/// <param name="FileFound">该日期是否存在日志文件（用于区分「没有记录」与「文件不存在」）。</param>
public sealed record AuditQueryResult(IReadOnlyList<AuditEntry> Entries, int SkippedLines, bool FileFound);
