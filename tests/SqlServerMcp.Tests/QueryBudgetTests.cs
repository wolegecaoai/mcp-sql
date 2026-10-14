using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlServerMcp.Configuration;
using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// 查询限额测试（真实 SQLite 查询路径）：offset 续取与按字符预算截断。
/// 用递归 CTE 在只读查询里直接造 100 行，不依赖任何写操作。
/// </summary>
public sealed class QueryBudgetTests : IDisposable
{
    private const string Recursive100RowsSql =
        "WITH RECURSIVE cnt(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < 100) SELECT x FROM cnt";

    private readonly string _dbPath;
    private readonly string _logDirectory;

    public QueryBudgetTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sqlservermcp-budget-{Guid.NewGuid():N}.db");
        _logDirectory = Path.Combine(Path.GetTempPath(), $"sqlservermcp-budgetlog-{Guid.NewGuid():N}");
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

    private DatabaseService CreateService(int maxResponseChars)
    {
        var options = new SqlServerOptions
        {
            MaxResponseChars = maxResponseChars,
            Databases = new Dictionary<string, DatabaseEntry>
            {
                ["ItDb"] = new DatabaseEntry
                {
                    Type = "sqlite",
                    // Pooling=False：SQLite 连接池会持有文件句柄，导致测试无法删除临时库
                    ConnectionString = $"Data Source={_dbPath};Pooling=False;"
                }
            }
        };

        return new DatabaseService(
            Options.Create(options),
            new DatabaseProviderFactory(),
            new SqlGuardService(),
            new AuditLogger(_logDirectory, NullLogger<AuditLogger>.Instance),
            NullLogger<DatabaseService>.Instance);
    }

    [Fact]
    public async Task 行数恰好等于上限时不算截断()
    {
        var result = await CreateService(60000).ExecuteQueryAsync("ItDb", Recursive100RowsSql, maxRows: 100);

        Assert.Equal(100, result.Rows.Count);
        Assert.False(result.Truncated);
        Assert.False(result.TruncatedBySize);
    }

    [Fact]
    public async Task 结果多于上限时按行截断()
    {
        var result = await CreateService(60000).ExecuteQueryAsync("ItDb", Recursive100RowsSql, maxRows: 30);

        Assert.Equal(30, result.Rows.Count);
        Assert.True(result.Truncated);
        Assert.False(result.TruncatedBySize);
    }

    [Fact]
    public async Task offset跳过结果前N行()
    {
        // 语义：offset 只影响起点，maxRows 仍是「本次返回的最大行数」
        //（与信封里 next.offset = skipped + row_count 的续取语义一致）
        var result = await CreateService(60000).ExecuteQueryAsync("ItDb", Recursive100RowsSql, maxRows: 10, offset: 4);

        Assert.Equal(10, result.Rows.Count);
        Assert.Equal(4, result.SkippedRows);
        Assert.Equal(5L, Convert.ToInt64(result.Rows[0]["x"]));   // 跳过 1..4，从 5 开始
        Assert.True(result.Truncated);   // 窗口之后仍有行，故标记可继续续取
    }

    [Fact]
    public async Task offset窗口落到结果末尾时不算截断()
    {
        var result = await CreateService(60000).ExecuteQueryAsync("ItDb", Recursive100RowsSql, maxRows: 10, offset: 95);

        Assert.Equal(5, result.Rows.Count);      // 只剩 95..100 共 5 行
        Assert.Equal(95, result.SkippedRows);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task offset超过结果集时返回空且报告实际跳过行数()
    {
        var result = await CreateService(60000).ExecuteQueryAsync("ItDb", Recursive100RowsSql, maxRows: 10, offset: 150);

        Assert.Empty(result.Rows);
        Assert.Equal(100, result.SkippedRows);   // 实际只跳过了 100 行
    }

    [Fact]
    public async Task 字符预算触发按体积截断而不是按行截断()
    {
        var result = await CreateService(300).ExecuteQueryAsync("ItDb", Recursive100RowsSql, maxRows: 100);

        Assert.True(result.Truncated);
        Assert.True(result.TruncatedBySize);
        Assert.InRange(result.Rows.Count, 1, 99);   // 至少保留一行，但未读满行数上限
    }

    [Fact]
    public async Task 字符预算越大返回行数越多()
    {
        var service = CreateService(3000);

        var small = await CreateService(300).ExecuteQueryAsync("ItDb", Recursive100RowsSql, maxRows: 100);
        var large = await service.ExecuteQueryAsync("ItDb", Recursive100RowsSql, maxRows: 100);

        Assert.True(large.Rows.Count > small.Rows.Count,
            $"预算更大应返回更多行：300→{small.Rows.Count} 行，3000→{large.Rows.Count} 行");
    }

    [Fact]
    public async Task 转义密集内容不会突破字符预算()
    {
        // 每行 20 个双引号：序列化后会转义成 40 字符（约原始长度 2 倍）。
        // 若按原始长度估算（旧实现），50 行会被误判为未超预算（I1）。
        const string sql =
            "SELECT replace(hex(zeroblob(20)), '00', '\"') AS q FROM " +
            "(WITH RECURSIVE cnt(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < 50) SELECT x FROM cnt)";

        var result = await CreateService(2000).ExecuteQueryAsync("ItDb", sql, maxRows: 50);

        var serialized = System.Text.Json.JsonSerializer.Serialize(
            result.Rows, DatabaseService.SerializerOptions);

        Assert.True(result.TruncatedBySize);
        Assert.True(serialized.Length <= 2000 * 1.1,
            $"行数据实际 {serialized.Length} 字符，应控制在预算 2000 附近");
    }

    [Fact]
    public async Task 单值超长被截断并留标记()
    {
        // 单值 3000 字符 > 预算 500：不截断的话一个超长列就能打爆上下文（I1）
        const string sql = "SELECT replace(hex(zeroblob(3000)), '00', 'x') AS big";

        var result = await CreateService(500).ExecuteQueryAsync("ItDb", sql, maxRows: 5);

        Assert.True(result.TruncatedBySize);
        var value = Assert.IsType<string>(result.Rows[0]["big"]);
        Assert.True(value.Length <= 500 + 40, $"单值应被截断到预算附近，实际 {value.Length} 字符");
        Assert.Contains("单值超长已截断", value);
    }

    [Fact]
    public async Task 预算为零表示不限制()
    {
        var result = await CreateService(0).ExecuteQueryAsync("ItDb", Recursive100RowsSql, maxRows: 100);

        Assert.Equal(100, result.Rows.Count);
        Assert.False(result.TruncatedBySize);
    }
}
