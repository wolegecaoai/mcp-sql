using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlServerMcp.Configuration;
using SqlServerMcp.Services;
using SqlServerMcp.Tools;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// 只读工具的对外契约测试（真实 SQLite 路径）：输出恒为合法 JSON、元数据齐全、
/// 方言不支持时下发 supported:false、list_databases 的富化字段与可选探测。
/// </summary>
public sealed class ReadToolsTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _logDirectory;
    private readonly DatabaseService _service;
    private readonly AuditLogger _auditLogger;

    public ReadToolsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sqlservermcp-readtools-{Guid.NewGuid():N}.db");
        _logDirectory = Path.Combine(Path.GetTempPath(), $"sqlservermcp-readtoolslog-{Guid.NewGuid():N}");

        var options = new SqlServerOptions
        {
            Databases = new Dictionary<string, DatabaseEntry>
            {
                ["ItDb"] = new DatabaseEntry
                {
                    Type = "sqlite",
                    ConnectionString = $"Data Source={_dbPath};Pooling=False;",
                    Description = "只读工具测试库"
                }
            }
        };

        _auditLogger = new AuditLogger(_logDirectory, NullLogger<AuditLogger>.Instance);
        _service = new DatabaseService(
            Options.Create(options),
            new DatabaseProviderFactory(),
            new SqlGuardService(),
            _auditLogger,
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

    /// <summary>用任意条目组合构造服务（用于验证多库探测与失败隔离）。</summary>
    private static DatabaseService CreateServiceWith(params (string Name, string Type, string ConnectionString)[] entries)
    {
        var databases = new Dictionary<string, DatabaseEntry>();
        foreach (var (name, type, connectionString) in entries)
        {
            databases[name] = new DatabaseEntry { Type = type, ConnectionString = connectionString };
        }

        return new DatabaseService(
            Options.Create(new SqlServerOptions { Databases = databases }),
            new DatabaseProviderFactory(),
            new SqlGuardService(),
            new AuditLogger(
                Path.Combine(Path.GetTempPath(), "sqlservermcp-readtools-aux-" + Guid.NewGuid().ToString("N")),
                NullLogger<AuditLogger>.Instance),
            NullLogger<DatabaseService>.Instance);
    }

    [Fact]
    public async Task list_databases_探测失败只影响该条目()
    {
        // 一个可用库 + 一个路径不存在的库：后者标记错误，前者仍能拿到版本（并行探测、单库隔离）
        var service = CreateServiceWith(
            ("Good", "sqlite", $"Data Source={_dbPath};Pooling=False;"),
            ("Bad", "sqlite", $"Data Source={Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid().ToString("N"), "db.file")};"));

        var text = await ReadTools.ListDatabasesAsync(service, includeServerInfo: true);

        var items = JsonDocument.Parse(text).RootElement.GetProperty("databases");
        var good = items.EnumerateArray().Single(item => item.GetProperty("database").GetString() == "Good");
        var bad = items.EnumerateArray().Single(item => item.GetProperty("database").GetString() == "Bad");

        Assert.True(good.GetProperty("connected").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(good.GetProperty("server_version").GetString()));
        Assert.False(bad.GetProperty("connected").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(bad.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task list_stored_procedures_不支持时不建立连接()
    {
        // 连接串故意指向不存在的路径：若实现仍去连库，就会返回连接错误而不是 supported:false
        var service = CreateServiceWith(
            ("Bad", "sqlite", $"Data Source={Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid().ToString("N"), "db.file")};"));

        var text = await ReadTools.ListStoredProceduresAsync(service, "Bad");

        var root = JsonDocument.Parse(text).RootElement;
        Assert.False(root.GetProperty("supported").GetBoolean());
        Assert.Contains("没有存储过程", root.GetProperty("notice").GetString());
    }

    [Fact]
    public async Task list_databases_含类型与DDL演练能力()
    {
        var text = await ReadTools.ListDatabasesAsync(_service, includeServerInfo: false);

        var root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(1, root.GetProperty("count").GetInt32());

        var entry = root.GetProperty("databases")[0];
        Assert.Equal("ItDb", entry.GetProperty("database").GetString());
        Assert.Equal("sqlite", entry.GetProperty("type").GetString());
        Assert.True(entry.GetProperty("ddl_rehearsal").GetBoolean());              // SQLite 的 DDL 可事务化
        Assert.False(entry.GetProperty("supports_stored_procedures").GetBoolean()); // SQLite 无存储过程
        Assert.False(root.GetProperty("include_server_info").GetBoolean());
        Assert.False(entry.TryGetProperty("server_version", out _));                // 未探测时不含版本
    }

    [Fact]
    public async Task list_databases_可选探测服务器信息()
    {
        var text = await ReadTools.ListDatabasesAsync(_service, includeServerInfo: true);

        var entry = JsonDocument.Parse(text).RootElement.GetProperty("databases")[0];

        Assert.True(entry.GetProperty("connected").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("server_version").GetString()));
        Assert.True(entry.TryGetProperty("latency_ms", out _));
    }

    [Fact]
    public async Task list_stored_procedures_对SQLite下发supported为false()
    {
        var text = await ReadTools.ListStoredProceduresAsync(_service, "ItDb");

        var root = JsonDocument.Parse(text).RootElement;
        Assert.False(root.GetProperty("supported").GetBoolean());
        Assert.Contains("没有存储过程", root.GetProperty("notice").GetString());
    }

    [Fact]
    public async Task execute_query_返回含元数据与列名的信封()
    {
        var text = await ReadTools.ExecuteQueryAsync(
            _service, _auditLogger, "ItDb", "SELECT 1 AS a, 'x' AS b");

        var root = JsonDocument.Parse(text).RootElement;
        Assert.Equal("ItDb", root.GetProperty("database").GetString());
        Assert.Equal("execute_query", root.GetProperty("tool").GetString());
        Assert.Equal(new[] { "a", "b" }, root.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(1, root.GetProperty("row_count").GetInt32());
    }

    [Fact]
    public async Task execute_query_截断时输出仍是合法JSON且带next()
    {
        const string sql = "WITH RECURSIVE cnt(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < 20) SELECT x FROM cnt";

        var text = await ReadTools.ExecuteQueryAsync(_service, _auditLogger, "ItDb", sql, maxRows: 5);

        var root = JsonDocument.Parse(text).RootElement;   // 旧实现在此处会解析失败
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Contains("已截断", root.GetProperty("notice").GetString());
        Assert.Equal(5, root.GetProperty("next").GetProperty("offset").GetInt32());
    }

    [Fact]
    public async Task execute_query_支持offset续取()
    {
        const string sql = "WITH RECURSIVE cnt(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < 20) SELECT x FROM cnt";

        var text = await ReadTools.ExecuteQueryAsync(_service, _auditLogger, "ItDb", sql, maxRows: 5, offset: 5);

        var root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(5, root.GetProperty("row_count").GetInt32());
        Assert.Equal(5, root.GetProperty("skipped").GetInt32());
        Assert.Equal(6, root.GetProperty("rows")[0].GetProperty("x").GetInt32());
    }

    [Fact]
    public async Task execute_query_紧凑形态行数与列数一致()
    {
        var text = await ReadTools.ExecuteQueryAsync(
            _service, _auditLogger, "ItDb", "SELECT 1 AS a, 2 AS b", responseFormat: ResponseFormatter.Compact);

        var rows = JsonDocument.Parse(text).RootElement.GetProperty("rows");
        Assert.Equal(2, rows[0].GetArrayLength());
    }

    [Fact]
    public async Task execute_query_非法响应格式被拒绝并列出可用值()
    {
        var ex = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() =>
            ReadTools.ExecuteQueryAsync(_service, _auditLogger, "ItDb", "SELECT 1", responseFormat: "xml"));

        Assert.Contains("compact", ex.Message);
    }

    [Fact]
    public async Task export_query_result_的json形态带截断信息()
    {
        const string sql = "WITH RECURSIVE cnt(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < 20) SELECT x FROM cnt";

        var text = await ReadTools.ExportQueryResultAsync(
            _service, _auditLogger, "ItDb", sql, format: "json", maxRows: 3);

        var root = JsonDocument.Parse(text).RootElement;
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Contains("已截断", root.GetProperty("notice").GetString());
    }

    [Fact]
    public async Task export_query_result_的csv保持纯净()
    {
        var text = await ReadTools.ExportQueryResultAsync(_service, _auditLogger, "ItDb", "SELECT 1 AS a", format: "csv");

        Assert.DoesNotContain("（注意", text);
    }

    [Fact]
    public async Task test_connection_返回版本与耗时()
    {
        var text = await ReadTools.TestConnectionAsync(_service, "ItDb");

        var root = JsonDocument.Parse(text).RootElement;
        Assert.Equal("ItDb", root.GetProperty("database").GetString());
        Assert.Equal("sqlite", root.GetProperty("provider").GetString());
        Assert.True(root.GetProperty("connected").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("server_version").GetString()));
    }
}
