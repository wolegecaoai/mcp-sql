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
