using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>AuditLogger 审计日志测试（JSON 行格式、字段完整性、SQL 截断与哈希）。</summary>
public sealed class AuditLoggerTests : IDisposable
{
    private readonly string _directory;

    public AuditLoggerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "sqlservermcp-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结果
        }
    }

    private AuditLogger CreateLogger() => new(_directory, NullLogger<AuditLogger>.Instance);

    private static JsonElement ReadSingleEntry(string directory)
    {
        var file = Directory.GetFiles(Path.Combine(directory, "logs"), "audit-*.log").Single();
        return JsonDocument.Parse(File.ReadAllText(file).Trim()).RootElement.Clone();
    }

    [Fact]
    public void LogWrite_写入JSON行含关键字段()
    {
        var logger = CreateLogger();
        logger.LogWrite("ZhouBaoTest", "sqlserver", "execute_non_query", committed: true, affectedRows: 3,
            sql: "UPDATE t SET a = 1 WHERE id = 2");

        var root = ReadSingleEntry(_directory);
        Assert.Equal("write", root.GetProperty("event").GetString());
        Assert.Equal("ZhouBaoTest", root.GetProperty("database").GetString());
        Assert.Equal("sqlserver", root.GetProperty("provider").GetString());
        Assert.Equal("execute_non_query", root.GetProperty("tool").GetString());
        Assert.Equal("commit", root.GetProperty("mode").GetString());
        Assert.Equal(3, root.GetProperty("affected_rows").GetInt32());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("sql_sha256").GetString()));
        Assert.Contains("UPDATE t SET a = 1", root.GetProperty("sql_preview").GetString());
    }

    [Fact]
    public void LogWrite_演练模式记录为dry_run()
    {
        var logger = CreateLogger();
        logger.LogWrite("db", "sqlite", "execute_ddl", committed: false, affectedRows: -1, sql: "CREATE TABLE t (a INT)");

        Assert.Equal("dry-run", ReadSingleEntry(_directory).GetProperty("mode").GetString());
    }

    [Fact]
    public void LogGuardRejected_记录事件与拒绝原因()
    {
        var logger = CreateLogger();
        logger.LogGuardRejected("ZhouBaoTest", "execute_query",
            "语句包含被禁止的危险关键词「DROP DATABASE」，已拒绝执行。", "DROP DATABASE x");

        var root = ReadSingleEntry(_directory);
        Assert.Equal("guard_rejected", root.GetProperty("event").GetString());
        Assert.Contains("DROP DATABASE", root.GetProperty("reason").GetString());
    }

    [Fact]
    public void SQL预览截断至200字符且不含完整长语句()
    {
        var logger = CreateLogger();
        var longSql = "SELECT '" + new string('x', 500) + "'";
        logger.LogWrite("db", "sqlserver", "execute_non_query", false, 1, longSql);

        var preview = ReadSingleEntry(_directory).GetProperty("sql_preview").GetString()!;
        Assert.True(preview.Length <= 201); // 200 字符 + 省略号
        Assert.DoesNotContain(new string('x', 300), preview);
    }

    [Fact]
    public void SQL哈希为64位小写十六进制()
    {
        var logger = CreateLogger();
        logger.LogWrite("db", "sqlserver", "execute_non_query", false, 1, "SELECT 1");

        var hash = ReadSingleEntry(_directory).GetProperty("sql_sha256").GetString()!;
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
        Assert.All(hash, c => Assert.True(Uri.IsHexDigit(c)));
    }

    [Fact]
    public void 时间戳为UTC可解析()
    {
        // M5：跨时区可排序、无本地时区歧义
        var logger = CreateLogger();
        logger.LogWrite("db", "sqlserver", "execute_non_query", false, 1, "SELECT 1");

        var ts = ReadSingleEntry(_directory).GetProperty("ts").GetString()!;
        var parsed = DateTimeOffset.Parse(ts, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.True(Math.Abs((DateTimeOffset.UtcNow - parsed).TotalMinutes) < 5);
    }

    [Fact]
    public void LogWriteFailed_记录失败事件_原因与工具名()
    {
        // I4/MI-10：执行阶段异常须留痕，且与其他事件一样含 tool 字段
        var logger = CreateLogger();
        logger.LogWriteFailed("db", "sqlite", "execute_non_query", "no such table: NotExist", "INSERT INTO NotExist (a) VALUES (1)");

        var root = ReadSingleEntry(_directory);
        Assert.Equal("write_failed", root.GetProperty("event").GetString());
        Assert.Equal("sqlite", root.GetProperty("provider").GetString());
        Assert.Equal("execute_non_query", root.GetProperty("tool").GetString());
        Assert.Contains("no such table", root.GetProperty("error").GetString());
    }

    [Fact]
    public void 多行日志每行独立可解析()
    {
        var logger = CreateLogger();
        logger.LogWrite("db", "sqlserver", "execute_non_query", true, 1, "INSERT INTO t VALUES (1)");
        logger.LogGuardRejected("db", "execute_query", "只读守卫拒绝", "DELETE FROM t");

        var file = Directory.GetFiles(Path.Combine(_directory, "logs"), "audit-*.log").Single();
        var lines = File.ReadAllLines(file).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => JsonDocument.Parse(line)); // 每行独立 JSON（含换行的 SQL 被转义）
    }
}
