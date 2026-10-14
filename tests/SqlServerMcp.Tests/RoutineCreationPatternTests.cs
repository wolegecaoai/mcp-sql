using ModelContextProtocol;
using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// CR-1 回归防线：例程/触发器/程序集创建语句的方言正则必须覆盖**全部语法变体**
/// （<c>CREATE OR ALTER</c> / <c>CREATE OR REPLACE</c> / <c>ALTER</c> / <c>TEMP</c> 等）——
/// 早期用固定短语匹配，会被修饰词绕过（如 <c>CREATE OR ALTER PROCEDURE</c> 不含子串 "CREATE PROCEDURE"）。
/// </summary>
public class RoutineCreationPatternTests
{
    private readonly SqlGuardService _guard = new();
    private readonly DatabaseProviderFactory _factory = new();

    private void AssertRejected(string providerId, string sql)
    {
        var provider = _factory.GetProvider(providerId);
        var ex = Assert.Throws<McpException>(() =>
            _guard.AssertNoRoutineCreation(sql, provider.RoutineCreationPattern, provider.DisplayName));
        Assert.Contains("例程", ex.Message);
    }

    private void AssertAllowed(string providerId, string sql)
    {
        var provider = _factory.GetProvider(providerId);
        _guard.AssertNoRoutineCreation(sql, provider.RoutineCreationPattern, provider.DisplayName);
    }

    [Fact]
    public void 四种方言均声明了例程创建规则()
    {
        foreach (var providerId in new[] { "sqlserver", "mysql", "postgresql", "sqlite" })
        {
            Assert.False(string.IsNullOrWhiteSpace(_factory.GetProvider(providerId).RoutineCreationPattern));
        }
    }

    [Theory]
    [InlineData("CREATE PROCEDURE p AS SELECT 1")]
    [InlineData("CREATE OR ALTER PROCEDURE p AS SELECT 1")]
    [InlineData("ALTER PROCEDURE p AS SELECT 1")]
    [InlineData("CREATE PROC p AS SELECT 1")]
    [InlineData("CREATE OR ALTER PROC p AS SELECT 1")]
    [InlineData("CREATE FUNCTION f() RETURNS INT AS BEGIN RETURN 1 END")]
    [InlineData("ALTER FUNCTION f() RETURNS INT AS BEGIN RETURN 2 END")]
    [InlineData("CREATE OR ALTER TRIGGER trg ON t AFTER INSERT AS SELECT 1")]
    [InlineData("CREATE ASSEMBLY a FROM 'x.dll'")]
    [InlineData("ALTER ASSEMBLY a FROM 'x.dll'")]
    [InlineData("CREATE OR ALTER AGGREGATE agg(@i INT) RETURNS INT EXTERNAL NAME x.y")]
    public void SqlServer_例程类语句全部拒绝(string sql) => AssertRejected("sqlserver", sql);

    [Theory]
    [InlineData("CREATE TABLE T (a INT)")]
    [InlineData("ALTER TABLE T ADD b INT")]
    [InlineData("SELECT * FROM sys.tables")]
    public void SqlServer_非例程语句放行(string sql) => AssertAllowed("sqlserver", sql);

    [Theory]
    [InlineData("CREATE PROCEDURE p() BEGIN SELECT 1; END")]
    [InlineData("CREATE OR REPLACE PROCEDURE p() BEGIN SELECT 1; END")]
    [InlineData("ALTER PROCEDURE p BEGIN SELECT 1; END")]
    [InlineData("CREATE FUNCTION f() RETURNS INT RETURN 1")]
    [InlineData("ALTER FUNCTION f RETURN 2")]
    [InlineData("CREATE TRIGGER trg BEFORE INSERT ON t FOR EACH ROW SET @x = 1")]
    [InlineData("CREATE EVENT e ON SCHEDULE EVERY 1 DAY DO DELETE FROM t")]
    [InlineData("ALTER EVENT e DISABLE")]
    public void MySql_例程类语句全部拒绝(string sql) => AssertRejected("mysql", sql);

    [Theory]
    [InlineData("CREATE TABLE T (a INT)")]
    [InlineData("ALTER TABLE T ADD COLUMN b INT")]
    [InlineData("SELECT 1")]
    [InlineData("CREATE INDEX idx ON T(a)")]
    public void MySql_非例程语句放行(string sql) => AssertAllowed("mysql", sql);

    [Theory]
    [InlineData("CREATE FUNCTION f() RETURNS int AS $$ SELECT 1 $$ LANGUAGE sql")]
    [InlineData("CREATE OR REPLACE FUNCTION f() RETURNS int AS $$ SELECT 1 $$ LANGUAGE plpgsql")]
    [InlineData("ALTER FUNCTION f() SET search_path = public")]
    [InlineData("CREATE PROCEDURE p() LANGUAGE sql AS $$ SELECT 1 $$")]
    [InlineData("CREATE TRIGGER trg BEFORE INSERT ON t FOR EACH ROW EXECUTE FUNCTION f()")]
    [InlineData("CREATE EXTENSION IF NOT EXISTS plpython3u")]
    [InlineData("CREATE SERVER srv FOREIGN DATA WRAPPER fdw")]
    [InlineData("ALTER SERVER srv OPTIONS (ADD host 'x')")]
    [InlineData("CREATE FOREIGN TABLE ft (a int) SERVER srv")]
    [InlineData("CREATE OR REPLACE LANGUAGE plperl")]
    [InlineData("CREATE USER MAPPING FOR u SERVER srv")]
    public void PostgreSql_例程类语句全部拒绝(string sql) => AssertRejected("postgresql", sql);

    [Theory]
    [InlineData("CREATE TABLE t (a int)")]
    [InlineData("ALTER TABLE t ADD COLUMN b int")]
    [InlineData("SELECT 1")]
    public void PostgreSql_非例程语句放行(string sql) => AssertAllowed("postgresql", sql);

    [Theory]
    [InlineData("CREATE TRIGGER trg AFTER INSERT ON t BEGIN SELECT 1; END")]
    [InlineData("CREATE TEMP TRIGGER trg AFTER INSERT ON t BEGIN SELECT 1; END")]
    [InlineData("CREATE TEMPORARY TRIGGER trg AFTER INSERT ON t BEGIN SELECT 1; END")]
    [InlineData("create trigger trg after insert on t begin select 1; end")]
    public void Sqlite_触发器全部拒绝(string sql) => AssertRejected("sqlite", sql);

    [Theory]
    [InlineData("CREATE TABLE t (a INT)")]
    [InlineData("CREATE INDEX idx ON t(a)")]
    [InlineData("SELECT 1")]
    public void Sqlite_非触发器语句放行(string sql) => AssertAllowed("sqlite", sql);

    [Fact]
    public void 注释分隔的变体同样命中()
    {
        // 清洗器把注释替换为空格，正则的 \s+ 因此能匹配 "CREATE /*绕过*/ OR ALTER PROCEDURE"
        AssertRejected("sqlserver", "CREATE /*绕过尝试*/ OR ALTER PROCEDURE p AS SELECT 1");
    }

    [Fact]
    public void 字符串字面量中的例程关键字不误伤()
    {
        // 守卫基于剥离字符串后的文本匹配，避免误伤字面量
        AssertAllowed("sqlserver", "SELECT 'CREATE PROCEDURE p AS SELECT 1' AS script_text");
    }
}
