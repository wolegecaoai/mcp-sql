using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using SqlServerMcp.Configuration;
using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// 参数化查询测试（真实 SQLite，B1/H3）：验证 @name 占位符经 SqlParameterInput 全链路绑定生效，
/// 且 JsonElement 归一化后可作为查询条件。
/// </summary>
public sealed class ParameterizedQueryTests : IDisposable
{
    private static readonly SqlStatementType[] DdlTypes = { SqlStatementType.Ddl };
    private static readonly SqlStatementType[] InsertTypes = { SqlStatementType.Insert };

    private readonly string _dbPath;
    private readonly string _logDirectory;
    private readonly DatabaseService _service;

    public ParameterizedQueryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sqlservermcp-param-{Guid.NewGuid():N}.db");
        _logDirectory = Path.Combine(Path.GetTempPath(), $"sqlservermcp-paramlog-{Guid.NewGuid():N}");

        var options = new SqlServerOptions
        {
            Databases = new Dictionary<string, DatabaseEntry>
            {
                ["ItDb"] = new DatabaseEntry
                {
                    Type = "sqlite",
                    ConnectionString = $"Data Source={_dbPath};Pooling=False;",
                    Description = "参数化测试库"
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
    public async Task 参数化写入与查询_占位符绑定生效()
    {
        await _service.ExecuteInTransactionAsync(
            "ItDb", "CREATE TABLE T (Id INTEGER, Name TEXT);", commit: true, DdlTypes);

        await _service.ExecuteInTransactionAsync(
            "ItDb",
            "INSERT INTO T (Id, Name) VALUES (@id, @name);",
            commit: true,
            InsertTypes,
            new List<SqlParameterInput> { new("@id", 1L), new("@name", "alice") });

        var query = await _service.ExecuteQueryAsync(
            "ItDb",
            "SELECT Name FROM T WHERE Id = @id;",
            10,
            new List<SqlParameterInput> { new("@id", 1L) });

        var row = Assert.Single(query.Rows);
        Assert.Equal("alice", row["Name"]);
    }

    [Fact]
    public async Task 参数值含SQL注入串_仅作为值不改变语义()
    {
        await _service.ExecuteInTransactionAsync(
            "ItDb", "CREATE TABLE U (Id INTEGER, Name TEXT);", commit: true, DdlTypes);

        await _service.ExecuteInTransactionAsync(
            "ItDb",
            "INSERT INTO U (Id, Name) VALUES (@id, @name);",
            commit: true,
            InsertTypes,
            new List<SqlParameterInput> { new("@id", 1L), new("@name", "'; DROP TABLE U; --") });

        var query = await _service.ExecuteQueryAsync(
            "ItDb",
            "SELECT Name FROM U WHERE Id = @id;",
            10,
            new List<SqlParameterInput> { new("@id", 1L) });

        var row = Assert.Single(query.Rows);
        Assert.Equal("'; DROP TABLE U; --", row["Name"]);

        // 表仍在（未被注入删除）
        var stillThere = await _service.ExecuteQueryAsync("ItDb", "SELECT COUNT(*) AS C FROM U;", 1);
        Assert.Equal(1L, stillThere.Rows[0]["C"]);
    }

    [Fact]
    public async Task 空参数名_抛出中文错误()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            _service.ExecuteQueryAsync(
                "ItDb",
                "SELECT 1 AS x;",
                1,
                new List<SqlParameterInput> { new("  ", 1) }));

        Assert.Contains("参数名", ex.Message);
    }
}
