using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using SqlServerMcp.Configuration;
using SqlServerMcp.Services;
using SqlServerMcp.Services.Providers;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// SQLite 临时库写路径集成测试：验证非 SQL Server Provider 在真实事务下的
/// confirm 二次确认（演练回滚/确认提交）、守卫下沉、版本探测与导出。
/// 弥补 MySQL 无凭据、PostgreSQL 无本地实例的覆盖缺口。
/// </summary>
public sealed class SqliteWriteIntegrationTests : IDisposable
{
    /// <summary>execute_non_query 的语句类型白名单。</summary>
    private static readonly SqlStatementType[] DmlTypes =
        { SqlStatementType.Insert, SqlStatementType.Update, SqlStatementType.Delete };

    /// <summary>execute_ddl 的语句类型白名单。</summary>
    private static readonly SqlStatementType[] DdlTypes = { SqlStatementType.Ddl };

    private readonly string _dbPath;
    private readonly string _logDirectory;
    private readonly DatabaseService _service;

    public SqliteWriteIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sqlservermcp-it-{Guid.NewGuid():N}.db");
        _logDirectory = Path.Combine(Path.GetTempPath(), $"sqlservermcp-itlogs-{Guid.NewGuid():N}");

        var options = new SqlServerOptions
        {
            Databases = new Dictionary<string, DatabaseEntry>
            {
                ["ItDb"] = new DatabaseEntry
                {
                    Type = "sqlite",
                    ConnectionString = $"Data Source={_dbPath};",
                    Description = "集成测试库"
                }
            }
        };

        _service = new DatabaseService(
            Options.Create(options),
            new DatabaseProviderFactory(),
            new SqlGuardService(),
            new AuditLogger(_logDirectory, NullLogger<AuditLogger>.Instance),
            NullLogger<DatabaseService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_dbPath);
            if (Directory.Exists(_logDirectory))
            {
                Directory.Delete(_logDirectory, recursive: true);
            }
        }
        catch
        {
            // 临时文件清理失败不影响测试结果
        }
    }

    [Fact]
    public async Task 建表_演练回滚后不存在_确认提交后存在()
    {
        const string createSql = "CREATE TABLE Note (Id INTEGER PRIMARY KEY, Title TEXT NOT NULL)";

        // DDL 的影响行数因驱动而异（SQL Server 为 -1，SQLite 为 0），此处只验证事务行为
        var dryRun = await _service.ExecuteInTransactionAsync("ItDb", createSql, commit: false, DdlTypes);
        Assert.True(dryRun is -1 or 0);

        var afterDryRun = await _service.ExecuteQueryAsync("ItDb",
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'Note'", 10);
        Assert.Empty(afterDryRun.Rows);

        var commit = await _service.ExecuteInTransactionAsync("ItDb", createSql, commit: true, DdlTypes);
        Assert.True(commit is -1 or 0);

        var afterCommit = await _service.ExecuteQueryAsync("ItDb",
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'Note'", 10);
        Assert.Single(afterCommit.Rows);
    }

    [Fact]
    public async Task 插入_演练回滚无行_确认提交有行()
    {
        await CreateNoteTableAsync();

        const string insertSql = "INSERT INTO Note (Id, Title) VALUES (1, '测试')";

        var dryRun = await _service.ExecuteInTransactionAsync("ItDb", insertSql, commit: false, DmlTypes);
        Assert.Equal(1, dryRun);
        Assert.Equal(0L, await CountRowsAsync());

        var commit = await _service.ExecuteInTransactionAsync("ItDb", insertSql, commit: true, DmlTypes);
        Assert.Equal(1, commit);
        Assert.Equal(1L, await CountRowsAsync());
    }

    [Fact]
    public async Task 只读守卫在SQLite上生效()
    {
        await CreateNoteTableAsync();

        var deleteEx = await Assert.ThrowsAsync<McpException>(() =>
            _service.ExecuteReadOnlyQueryAsync("ItDb", "DELETE FROM Note", 10));
        Assert.Contains("仅支持 SELECT/WITH", deleteEx.Message);

        var intoEx = await Assert.ThrowsAsync<McpException>(() =>
            _service.ExecuteReadOnlyQueryAsync("ItDb", "SELECT * INTO NoteCopy FROM Note", 10));
        Assert.Contains("SELECT ... INTO", intoEx.Message);
    }

    [Fact]
    public async Task 版本探测与导出在SQLite上工作()
    {
        var version = await _service.ExecuteQueryAsync("ItDb", new SqliteProvider().BuildServerVersionSql(), 1);
        Assert.Contains(".", Assert.IsType<string>(version.Rows[0]["server_version"]));

        await CreateNoteTableAsync();
        await _service.ExecuteInTransactionAsync("ItDb", "INSERT INTO Note (Id, Title) VALUES (1, '含,逗号')", commit: true, DmlTypes);

        var result = await _service.ExecuteQueryAsync("ItDb", "SELECT Id, Title FROM Note", 10);
        var csv = ExportFormatter.ToCsv(result);
        Assert.Contains("\"含,逗号\"", csv); // 含逗号字段被正确加引号
    }

    [Fact]
    public async Task 服务层兜底校验_黑名单纯拒绝而非连接失败()
    {
        // I2/IM-3：绕过工具层直接调用写方法，服务层须给出**守卫拒绝**（而非连接/执行失败造成假阳性）
        var blacklistEx = await Assert.ThrowsAsync<McpException>(() =>
            _service.ExecuteInTransactionAsync("ItDb", "DROP DATABASE x", commit: false, DdlTypes));
        Assert.Contains("危险关键词", blacklistEx.Message);

        var multiEx = await Assert.ThrowsAsync<McpException>(() =>
            _service.ExecuteInTransactionAsync("ItDb", "SELECT 1; SELECT 2", commit: false, DmlTypes));
        Assert.Contains("只允许执行一条", multiEx.Message);

        // 类型白名单：DDL 走 execute_non_query 的允许集时被拒
        var typeEx = await Assert.ThrowsAsync<McpException>(() =>
            _service.ExecuteInTransactionAsync("ItDb", "CREATE TABLE X (a INT)", commit: false, DmlTypes));
        Assert.Contains("不支持执行", typeEx.Message);
        Assert.Contains("DDL", typeEx.Message);
    }

    [Fact]
    public async Task 服务层拦截例程与触发器创建()
    {
        // CR-1：SQLite 触发器体可藏危险词，服务层必须拒绝（含 TEMP/TEMPORARY 变体）。
        // 注：用例刻意不含内部分号——含分号的例行体更早被单语句规则拦下（见 AGENTS.md 已知限制）
        foreach (var sql in new[]
                 {
                     "CREATE TRIGGER trg AFTER INSERT ON Note BEGIN SELECT 1 END",
                     "CREATE TEMP TRIGGER trg AFTER INSERT ON Note BEGIN SELECT 1 END",
                     "CREATE TEMPORARY TRIGGER trg AFTER INSERT ON Note BEGIN SELECT 1 END"
                 })
        {
            var ex = await Assert.ThrowsAsync<McpException>(() =>
                _service.ExecuteInTransactionAsync("ItDb", sql, commit: false, DdlTypes));
            Assert.Contains("触发器", ex.Message);
        }
    }

    [Fact]
    public async Task 写执行失败入审计日志且带工具名()
    {
        // I4/MI-10：执行阶段异常（表不存在）须留痕，且含 tool 字段
        await Assert.ThrowsAsync<McpException>(() =>
            _service.ExecuteInTransactionAsync("ItDb", "INSERT INTO NotExistTable (a) VALUES (1)", commit: true, DmlTypes));

        var logFile = Directory.GetFiles(Path.Combine(_logDirectory, "logs"), "audit-*.log").Single();
        var content = File.ReadAllText(logFile);
        Assert.Contains("write_failed", content);
        Assert.Contains("\"tool\":\"execute_write\"", content);
    }

    [Fact]
    public async Task 守卫拒绝入审计日志()
    {
        // MI-3：服务层守卫拒绝也须留痕（工具层未拦截时）
        await Assert.ThrowsAsync<McpException>(() =>
            _service.ExecuteInTransactionAsync("ItDb", "DROP DATABASE x", commit: false, DdlTypes));

        var logFile = Directory.GetFiles(Path.Combine(_logDirectory, "logs"), "audit-*.log").Single();
        Assert.Contains("guard_rejected", File.ReadAllText(logFile));
    }

    private Task CreateNoteTableAsync() =>
        _service.ExecuteInTransactionAsync(
            "ItDb", "CREATE TABLE Note (Id INTEGER PRIMARY KEY, Title TEXT NOT NULL)", commit: true, DdlTypes);

    private async Task<long> CountRowsAsync()
    {
        var result = await _service.ExecuteQueryAsync("ItDb", "SELECT COUNT(*) AS c FROM Note", 1);
        return Convert.ToInt64(result.Rows[0]["c"]);
    }
}
