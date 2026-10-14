using Microsoft.Extensions.Logging.Abstractions;
using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// AuditLogger 保留策略测试：只清理「文件名日期已过期」的已完成日文件，
/// 当日文件、无法识别日期的文件、目录内其他文件一律不碰。
/// </summary>
public sealed class AuditLoggerRetentionTests : IDisposable
{
    private readonly string _directory;
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.UtcNow);

    public AuditLoggerRetentionTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "sqlservermcp-auditr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_directory, "logs"));
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

    private string CreateLogFile(DateOnly date, string content = "{}")
    {
        var path = Path.Combine(_directory, "logs", $"audit-{date:yyyyMMdd}.log");
        File.WriteAllText(path, content);
        return path;
    }

    private string CreateArbitraryFile(string fileName)
    {
        var path = Path.Combine(_directory, "logs", fileName);
        File.WriteAllText(path, "{}");
        return path;
    }

    [Fact]
    public void 清理_删除过期文件并保留保留期内的文件()
    {
        var today = CreateLogFile(_today);
        var yesterday = CreateLogFile(_today.AddDays(-1));
        var withinRetention = CreateLogFile(_today.AddDays(-29));
        var justExpired = CreateLogFile(_today.AddDays(-31));
        var longExpired = CreateLogFile(_today.AddDays(-100));

        var removed = CreateLogger().PruneOldFiles(30);

        Assert.Equal(2, removed);
        Assert.False(File.Exists(justExpired));
        Assert.False(File.Exists(longExpired));
        Assert.True(File.Exists(today));
        Assert.True(File.Exists(yesterday));
        Assert.True(File.Exists(withinRetention));
    }

    [Fact]
    public void 清理_当日文件即使保留期为1天也不删除()
    {
        var today = CreateLogFile(_today);
        var yesterday = CreateLogFile(_today.AddDays(-1));

        var removed = CreateLogger().PruneOldFiles(1);

        Assert.Equal(1, removed);
        Assert.True(File.Exists(today));
        Assert.False(File.Exists(yesterday));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void 清理_保留期非正数时不删除任何文件(int retentionDays)
    {
        var ancient = CreateLogFile(_today.AddDays(-1000));

        var removed = CreateLogger().PruneOldFiles(retentionDays);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(ancient));
    }

    [Theory]
    [InlineData("audit-notadate.log")]
    [InlineData("audit-.log")]
    [InlineData("audit-20261301.log")]     // 非法月份
    [InlineData("audit-20000101.txt")]     // 后缀不匹配
    [InlineData("other-20000101.log")]     // 前缀不匹配
    [InlineData("appsettings.json")]
    public void 清理_文件名不匹配的文件永不删除(string fileName)
    {
        var path = CreateArbitraryFile(fileName);

        var removed = CreateLogger().PruneOldFiles(1);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void 清理_日志目录不存在时返回0()
    {
        var missing = Path.Combine(Path.GetTempPath(), "sqlservermcp-auditr-missing-" + Guid.NewGuid().ToString("N"));

        var removed = new AuditLogger(missing, NullLogger<AuditLogger>.Instance).PruneOldFiles(1);

        Assert.Equal(0, removed);
    }

    [Fact]
    public void 清理_不影响查询读取当日文件()
    {
        var logger = CreateLogger();
        logger.LogWrite("Db", "sqlite", "execute_non_query", committed: true, affectedRows: 1, sql: "INSERT INTO t VALUES (1)");

        logger.PruneOldFiles(1);

        var result = logger.QueryEntries(DateOnly.FromDateTime(DateTime.UtcNow), null, null, null, 10);
        Assert.Single(result.Entries);
        Assert.Equal("Db", result.Entries[0].Database);
    }
}
