using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// AuditLogger 查询能力测试（按日期定位文件、库/事件/工具过滤、条数上限保留最新、脏行容错）。
/// 直接写原始 JSON 行以精确控制日期与内容，从而独立验证「查询」而非「写入」。
/// </summary>
public sealed class AuditLoggerQueryTests : IDisposable
{
    private readonly string _directory;
    private readonly DateOnly _date = new(2026, 10, 14);

    public AuditLoggerQueryTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "sqlservermcp-auditq-" + Guid.NewGuid().ToString("N"));
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

    private void WriteLines(DateOnly date, params string[] lines)
    {
        var directory = Path.Combine(_directory, "logs");
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, $"audit-{date:yyyyMMdd}.log"), lines);
    }

    private static string Line(
        string timestamp,
        string eventType,
        string? database = null,
        string? provider = null,
        string? tool = null,
        string? mode = null,
        long? affectedRows = null,
        string? reason = null,
        string? error = null)
    {
        var entry = new Dictionary<string, object?>
        {
            ["ts"] = timestamp,
            ["event"] = eventType
        };

        if (database is not null) entry["database"] = database;
        if (provider is not null) entry["provider"] = provider;
        if (tool is not null) entry["tool"] = tool;
        if (mode is not null) entry["mode"] = mode;
        if (affectedRows is not null) entry["affected_rows"] = affectedRows;
        if (reason is not null) entry["reason"] = reason;
        if (error is not null) entry["error"] = error;

        return JsonSerializer.Serialize(entry);
    }

    [Fact]
    public void 查询_文件不存在时返回FileFound为false()
    {
        var result = CreateLogger().QueryEntries(_date, null, null, null, 10);

        Assert.False(result.FileFound);
        Assert.Empty(result.Entries);
        Assert.Equal(0, result.SkippedLines);
    }

    [Fact]
    public void 查询_空文件返回空列表但FileFound为true()
    {
        WriteLines(_date);

        var result = CreateLogger().QueryEntries(_date, null, null, null, 10);

        Assert.True(result.FileFound);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void 查询_仅返回指定日期的文件()
    {
        WriteLines(_date, Line("2026-10-14T01:00:00+00:00", "write", "A", tool: "execute_ddl"));
        WriteLines(_date.AddDays(-1), Line("2026-10-13T01:00:00+00:00", "write", "B", tool: "execute_ddl"));

        var result = CreateLogger().QueryEntries(_date, null, null, null, 10);

        Assert.Single(result.Entries);
        Assert.Equal("A", result.Entries[0].Database);
    }

    [Fact]
    public void 查询_按库_事件类型与工具过滤()
    {
        WriteLines(_date,
            Line("2026-10-14T01:00:00+00:00", "write", "PLC_DB", "mysql", "execute_ddl", mode: "commit", affectedRows: 0),
            Line("2026-10-14T01:01:00+00:00", "write", "ZhouBaoTest", "sqlserver", "execute_non_query", mode: "dry-run", affectedRows: 3),
            Line("2026-10-14T01:02:00+00:00", "guard_rejected", "PLC_DB", "mysql", "execute_ddl", reason: "检测到危险关键词"),
            Line("2026-10-14T01:03:00+00:00", "write_failed", "PLC_DB", "mysql", "execute_non_query", error: "no such table"));

        var logger = CreateLogger();

        Assert.Equal(3, logger.QueryEntries(_date, "PLC_DB", null, null, 10).Entries.Count);
        Assert.Single(logger.QueryEntries(_date, null, "guard_rejected", null, 10).Entries);
        Assert.Equal(2, logger.QueryEntries(_date, null, null, "execute_ddl", 10).Entries.Count);
        Assert.Single(logger.QueryEntries(_date, "plc_db", "write", "execute_ddl", 10).Entries);   // 大小写不敏感
    }

    [Fact]
    public void 查询_过滤条件同时生效时为交集()
    {
        WriteLines(_date,
            Line("2026-10-14T01:00:00+00:00", "write", "PLC_DB", tool: "execute_ddl"),
            Line("2026-10-14T01:01:00+00:00", "write", "PLC_DB", tool: "execute_non_query"),
            Line("2026-10-14T01:02:00+00:00", "guard_rejected", "PLC_DB", tool: "execute_ddl"));

        var result = CreateLogger().QueryEntries(_date, "PLC_DB", "write", "execute_ddl", 10);

        Assert.Single(result.Entries);
        Assert.Equal("2026-10-14T01:00:00+00:00", result.Entries[0].Ts);
    }

    [Fact]
    public void 查询_条数上限保留最新的若干条()
    {
        WriteLines(_date,
            Line("2026-10-14T01:00:00+00:00", "write", "Db1"),
            Line("2026-10-14T02:00:00+00:00", "write", "Db2"),
            Line("2026-10-14T03:00:00+00:00", "write", "Db3"),
            Line("2026-10-14T04:00:00+00:00", "write", "Db4"),
            Line("2026-10-14T05:00:00+00:00", "write", "Db5"));

        var result = CreateLogger().QueryEntries(_date, null, null, null, 2);

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("Db4", result.Entries[0].Database);   // 保留最新 2 条，且顺序仍为时间先后
        Assert.Equal("Db5", result.Entries[1].Database);
    }

    [Fact]
    public void 查询_脏行跳过并计数且不影响合法条目()
    {
        WriteLines(_date,
            Line("2026-10-14T01:00:00+00:00", "write", "Good1"),
            "这不是 JSON",
            "{ \"unclosed\": ",
            "12345",
            "{\"ts\":\"2026-10-14T02:00:00+00:00\"}",
            "",
            Line("2026-10-14T03:00:00+00:00", "write", "Good2"));

        var result = CreateLogger().QueryEntries(_date, null, null, null, 10);

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("Good1", result.Entries[0].Database);
        Assert.Equal("Good2", result.Entries[1].Database);
        // 脏行：非 JSON、截断 JSON、数字、缺 event 字段
        Assert.Equal(4, result.SkippedLines);
    }

    [Fact]
    public async Task 查询_与并发写入互不阻塞且不丢记录()
    {
        // I2 回归：读取若只共享 FileShare.Read，查询期间并发的审计写入会因共享冲突失败，
        // 被 AppendEntry 降级为警告 → 悄悄丢审计。修复前此用例会丢掉部分条目。
        // 注：这里用「今天」的日期（LogWrite 按 UTC 当日切文件），不能用固定的 _date。
        const int existing = 2000;   // 预置大文件，让单次读取有一定耗时，从而与写入真正重叠
        const int appended = 200;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var logsDirectory = Path.Combine(_directory, "logs");
        Directory.CreateDirectory(logsDirectory);
        var logFile = Path.Combine(logsDirectory, $"audit-{today:yyyyMMdd}.log");
        File.WriteAllLines(logFile, Enumerable.Range(0, existing)
            .Select(i => Line($"2026-10-14T00:00:{i % 60:00}+00:00", "write", "Seed")));

        var start = new ManualResetEventSlim(false);

        var writer = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < appended; i++)
            {
                CreateLogger().LogWrite("Db", "sqlite", "execute_non_query", committed: true, affectedRows: i, sql: $"INSERT INTO t VALUES ({i})");
            }
        });

        var reader = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < 400; i++)
            {
                CreateLogger().QueryEntries(today, null, null, null, 10);
            }
        });

        start.Set();
        await Task.WhenAll(writer, reader);

        // 断言「一条审计都没丢」——这是本用例真正要守住的性质
        Assert.Equal(existing + appended, File.ReadAllLines(logFile).Length);
        Assert.Equal(appended, CreateLogger().QueryEntries(today, "Db", null, null, appended + 10).Entries.Count);
    }

    [Fact]
    public void 查询_投影字段完整()
    {
        WriteLines(_date, Line("2026-10-14T01:00:00+00:00", "write", "PLC_DB", "mysql", "execute_ddl", "commit", 7));

        var entry = Assert.Single(CreateLogger().QueryEntries(_date, null, null, null, 10).Entries);

        Assert.Equal("2026-10-14T01:00:00+00:00", entry.Ts);
        Assert.Equal("write", entry.Event);
        Assert.Equal("PLC_DB", entry.Database);
        Assert.Equal("mysql", entry.Provider);
        Assert.Equal("execute_ddl", entry.Tool);
        Assert.Equal("commit", entry.Mode);
        Assert.Equal(7, entry.AffectedRows);
    }
}
