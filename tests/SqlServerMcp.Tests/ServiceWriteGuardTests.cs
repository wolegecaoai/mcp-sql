using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using SqlServerMcp.Configuration;
using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// 服务层写路径守卫测试（I2/I3/M1/M6）：<b>不建立任何真实数据库连接</b>——
/// 这些校验都发生在连接之前，因此可在无 MySQL/PG 实例的环境下验证方言相关的 fail-closed 行为。
/// </summary>
public sealed class ServiceWriteGuardTests : IDisposable
{
    private static readonly SqlStatementType[] DmlTypes =
        { SqlStatementType.Insert, SqlStatementType.Update, SqlStatementType.Delete };

    private static readonly SqlStatementType[] DdlTypes = { SqlStatementType.Ddl };

    private readonly string _logDirectory;

    public ServiceWriteGuardTests()
    {
        _logDirectory = Path.Combine(Path.GetTempPath(), $"sqlservermcp-guard-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_logDirectory))
            {
                Directory.Delete(_logDirectory, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响测试结果
        }
    }

    private DatabaseService CreateService(params (string Name, string Type, string ConnectionString)[] entries)
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
            new AuditLogger(_logDirectory, NullLogger<AuditLogger>.Instance),
            NullLogger<DatabaseService>.Instance);
    }

    [Fact]
    public async Task MySQL的DDL演练模式被拒绝且不建立连接()
    {
        // I3：MySQL DDL 隐式提交不可演练——fail-closed 拒绝，且判定发生在连接之前
        // （连接串故意指向不存在的端口，若实现先去连接则会报连接错误而非本提示）
        var service = CreateService(("MyDb", "mysql", "Server=127.0.0.1;Port=59999;Database=m;User=root;Password=x;"));

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            service.ExecuteInTransactionAsync("MyDb", "CREATE TABLE T (a INT)", commit: false, DdlTypes));

        Assert.Contains("无法回滚", ex.Message);
    }

    [Fact]
    public async Task PostgreSql的DDL演练可达连接阶段()
    {
        // 对照：PG 支持事务化 DDL，演练不应被方言分支拒绝（此处会因无实例而报连接错误，属预期）
        var service = CreateService(("PgDb", "postgresql", "Host=127.0.0.1;Port=59998;Database=p;Username=postgres;Password=x;"));

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            service.ExecuteInTransactionAsync("PgDb", "CREATE TABLE T (a INT)", commit: false, DdlTypes));

        Assert.DoesNotContain("无法回滚", ex.Message);
    }

    [Fact]
    public async Task 服务层拦截多语句与黑名单并给出守卫原因()
    {
        // I2/IM-3：绕过工具层直接调用服务方法仍被拦截，且断言**拒绝原因**而非仅异常类型
        //（连接串指向不存在的端口，若断言仅看类型则"连接失败"也会让用例通过——假阳性）
        var service = CreateService(("MyDb", "mysql", "Server=127.0.0.1;Port=59999;Database=m;"));

        var blacklistEx = await Assert.ThrowsAsync<McpException>(() =>
            service.ExecuteInTransactionAsync("MyDb", "DROP DATABASE x", commit: true, DdlTypes));
        Assert.Contains("危险关键词", blacklistEx.Message);

        var multiEx = await Assert.ThrowsAsync<McpException>(() =>
            service.ExecuteInTransactionAsync("MyDb", "SELECT 1; SELECT 2", commit: false, DmlTypes));
        Assert.Contains("只允许执行一条", multiEx.Message);

        var typeEx = await Assert.ThrowsAsync<McpException>(() =>
            service.ExecuteInTransactionAsync("MyDb", "CREATE TABLE T (a INT)", commit: false, DmlTypes));
        Assert.Contains("不支持执行", typeEx.Message);
    }

    [Fact]
    public async Task 服务层拦截例程创建变体()
    {
        // CR-1：MySQL 的 ALTER PROCEDURE / CREATE OR REPLACE FUNCTION 等变体不得绕过
        var service = CreateService(("MyDb", "mysql", "Server=127.0.0.1;Port=59999;Database=m;"));

        // 用例不含内部分号（含分号的例行体会更早被单语句规则拦下）
        foreach (var sql in new[]
                 {
                     "CREATE PROCEDURE p() BEGIN SELECT 1 END",
                     "CREATE OR REPLACE PROCEDURE p() BEGIN SELECT 1 END",
                     "ALTER PROCEDURE p BEGIN SELECT 1 END",
                     "CREATE FUNCTION f() RETURNS INT RETURN 1",
                     "ALTER FUNCTION f RETURN 2",
                     "CREATE TRIGGER trg BEFORE INSERT ON t FOR EACH ROW SET @x = 1"
                 })
        {
            var ex = await Assert.ThrowsAsync<McpException>(() =>
                service.ExecuteInTransactionAsync("MyDb", sql, commit: false, DdlTypes));
            Assert.Contains("例程", ex.Message);
        }
    }

    [Fact]
    public void 数据库别名仅大小写不同时给出中文提示()
    {
        // M1：大小写不敏感字典遇重复键须转为可操作提示而非裸 ArgumentException
        var ex = Assert.Throws<McpException>(() => CreateService(
            ("Db", "sqlite", "Data Source=:memory:"),
            ("db", "sqlite", "Data Source=:memory:")));

        Assert.Contains("大小写", ex.Message);
    }

    [Fact]
    public void 合并黑名单按Provider缓存()
    {
        // M6：同一 Provider 反复取用返回同一实例，避免每次校验重新 Concat 分配
        var service = CreateService(
            ("A", "mysql", "Server=x;Database=a;"),
            ("B", "mysql", "Server=x;Database=b;"));

        Assert.Same(service.GetEffectiveDangerousKeywords("A"), service.GetEffectiveDangerousKeywords("B"));
    }

    [Fact]
    public void 附加黑名单已并入合并结果()
    {
        var service = CreateService(("MyDb", "mysql", "Server=x;Database=m;"));
        var keywords = service.GetEffectiveDangerousKeywords("MyDb");

        Assert.Contains("DROP DATABASE", keywords);   // 全局
        Assert.Contains("CREATE USER", keywords);      // MySQL 附加
    }
}
