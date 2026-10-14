using ModelContextProtocol;
using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>SqlGuardService 安全守卫表驱动测试（C1/I1/I11 修复的回归防线）。</summary>
public class SqlGuardServiceTests
{
    private readonly SqlGuardService _guard = new();

    private static readonly string[] NoKeywords = Array.Empty<string>();

    // ---------- 语句分类 ----------

    [Theory]
    [InlineData("SELECT 1", SqlStatementType.Select)]
    [InlineData("SELECT * FROM t WHERE x = 1", SqlStatementType.Select)]
    [InlineData("WITH cte AS (SELECT 1 AS x) SELECT * FROM cte", SqlStatementType.Select)]
    [InlineData("WITH a AS (SELECT 1), b AS (SELECT 2) SELECT * FROM a JOIN b ON 1=1", SqlStatementType.Select)]
    [InlineData("WITH RECURSIVE r AS (SELECT 1) SELECT * FROM r", SqlStatementType.Select)]
    [InlineData("INSERT INTO t (a) VALUES (1)", SqlStatementType.Insert)]
    [InlineData("UPDATE t SET a = 1", SqlStatementType.Update)]
    [InlineData("DELETE FROM t", SqlStatementType.Delete)]
    [InlineData("MERGE INTO t USING s ON 1=1 WHEN MATCHED THEN UPDATE SET a = 1", SqlStatementType.Insert)]
    [InlineData("CREATE TABLE t (a INT)", SqlStatementType.Ddl)]
    [InlineData("ALTER TABLE t ADD b INT", SqlStatementType.Ddl)]
    [InlineData("DROP TABLE t", SqlStatementType.Ddl)]
    [InlineData("TRUNCATE TABLE t", SqlStatementType.Ddl)]
    [InlineData("GRANT SELECT ON t TO u", SqlStatementType.Ddl)]
    [InlineData("EXEC sp_who", SqlStatementType.Other)]
    [InlineData("sp_executesql N'SELECT 1'", SqlStatementType.Other)]
    [InlineData("", SqlStatementType.Other)]
    public void ClassifyStatement_按首关键字与WITH穿透正确分类(string sql, SqlStatementType expected)
    {
        Assert.Equal(expected, _guard.ClassifyStatement(sql));
    }

    [Theory]
    [InlineData("WITH cte AS (SELECT 1 AS x) INSERT INTO t (a) SELECT x FROM cte", SqlStatementType.Insert)]
    [InlineData("WITH cte AS (SELECT 1 AS x) DELETE FROM t WHERE a = x", SqlStatementType.Delete)]
    [InlineData("WITH cte AS (SELECT 1 AS x) UPDATE t SET a = x", SqlStatementType.Update)]
    [InlineData("WITH a AS (SELECT 1), b AS (SELECT 2) DELETE FROM t WHERE a IN (SELECT 1 FROM a)", SqlStatementType.Delete)]
    public void ClassifyStatement_WITH穿透识别写操作(string sql, SqlStatementType expected)
    {
        Assert.Equal(expected, _guard.ClassifyStatement(sql));
    }

    // ---------- 只读守卫（含 C1/I1 修复） ----------

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("SELECT * FROM t WHERE x = 1")]
    [InlineData("WITH cte AS (SELECT 1 AS x) SELECT * FROM cte")]
    [InlineData("SELECT * FROM t WHERE name = 'INTO'")]              // 字符串常量不误伤
    [InlineData("SELECT [into] FROM t")]                             // 方括号标识符不误伤
    [InlineData("SELECT `into` FROM t")]                             // 反引号标识符不误伤
    public void AssertReadOnly_合法查询通过(string sql)
    {
        _guard.AssertReadOnly(sql, NoKeywords);
    }

    [Theory]
    [InlineData("DELETE FROM t")]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("CREATE TABLE t (a INT)")]
    [InlineData("WITH cte AS (SELECT 1) INSERT INTO t SELECT 1")]
    [InlineData("WITH cte AS (SELECT 1) DELETE FROM t")]
    [InlineData("SELECT * INTO NewTable FROM t")]                    // SELECT INTO 建表写数据
    [InlineData("EXEC sp_who")]
    public void AssertReadOnly_非只读语句拒绝(string sql)
    {
        Assert.Throws<McpException>(() => _guard.AssertReadOnly(sql, NoKeywords));
    }

    [Fact]
    public void AssertReadOnly_MySQL可执行注释拒绝()
    {
        // C1：/*! ... */ 内容会被 MySQL 服务器执行，守卫看到的与实际执行的不一致
        Assert.Throws<McpException>(() =>
            _guard.AssertReadOnly("SELECT 1 /*!80000 INTO OUTFILE '/tmp/leak.txt' */", NoKeywords));
    }

    [Fact]
    public void AssertReadOnly_未终止字符串字面量拒绝()
    {
        Assert.Throws<McpException>(() => _guard.AssertReadOnly("SELECT * FROM t WHERE x = 'abc", NoKeywords));
    }

    [Fact]
    public void AssertReadOnly_未终止块注释拒绝()
    {
        Assert.Throws<McpException>(() => _guard.AssertReadOnly("SELECT 1 /* 没有结束", NoKeywords));
    }

    [Fact]
    public void AssertReadOnly_黑名单在只读路径生效()
    {
        // I1：SELECT 也可通过链接服务器/文件函数外发数据
        var keywords = new[] { "OPENROWSET", "dblink", "pg_read_file" };
        Assert.Throws<McpException>(() =>
            _guard.AssertReadOnly("SELECT * FROM OPENROWSET('SQLNCLI', 'srv', 'SELECT 1')", keywords));
        Assert.Throws<McpException>(() =>
            _guard.AssertReadOnly("SELECT * FROM dblink('host=x', 'SELECT 1') AS t(a INT)", keywords));
        Assert.Throws<McpException>(() =>
            _guard.AssertReadOnly("SELECT pg_read_file('/etc/passwd')", keywords));
    }

    // ---------- 单语句校验 ----------

    [Theory]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("INSERT INTO t VALUES (1); DROP TABLE t")]
    [InlineData("SELECT 1 /*!; DROP TABLE t */")]                    // 可执行注释夹带分号（C1 场景）
    public void AssertSingleStatement_多语句拒绝(string sql)
    {
        Assert.Throws<McpException>(() => _guard.AssertSingleStatement(sql, "test"));
    }

    [Theory]
    [InlineData("SELECT ';' AS x")]                                  // 字符串内分号不切分
    [InlineData("SELECT 1;")]                                        // 尾部分号允许
    public void AssertSingleStatement_单语句通过(string sql)
    {
        _guard.AssertSingleStatement(sql, "test");
    }

    [Fact]
    public void AssertSingleStatement_空语句拒绝()
    {
        Assert.Throws<McpException>(() => _guard.AssertSingleStatement("   ", "test"));
    }

    // ---------- 黑名单 ----------

    [Theory]
    [InlineData("DROP DATABASE x")]
    [InlineData("drop database x")]                                  // 大小写不敏感
    [InlineData("DROP/*x*/DATABASE x")]                              // 注释插词间
    [InlineData("DROP\nDATABASE x")]                                 // 空白变体
    [InlineData("CREATE LOGIN u WITH PASSWORD = 'x'")]
    [InlineData("GRANT CONTROL SERVER TO u")]
    public void AssertNoDangerousKeywords_命中拒绝(string sql)
    {
        var keywords = new[] { "DROP DATABASE", "CREATE LOGIN", "GRANT" };
        Assert.Throws<McpException>(() => _guard.AssertNoDangerousKeywords(sql, keywords));
    }

    [Theory]
    [InlineData("SELECT * FROM t WHERE remark = 'DROP DATABASE'")]   // 字符串常量豁免
    [InlineData("SELECT [DROP DATABASE] FROM t")]                    // 方括号标识符豁免
    public void AssertNoDangerousKeywords_字符串与标识符不误伤(string sql)
    {
        var keywords = new[] { "DROP DATABASE" };
        _guard.AssertNoDangerousKeywords(sql, keywords);
    }

    // ---------- 无 WHERE 判定（I11 顶层深度） ----------

    [Theory]
    [InlineData("UPDATE t SET a = 1", true)]
    [InlineData("DELETE FROM t", true)]
    [InlineData("UPDATE t SET a = (SELECT MAX(x) FROM u WHERE y = 1)", true)]   // 假阴性修复：子查询 WHERE 不算
    [InlineData("UPDATE t SET a = 1 WHERE b = 2", false)]
    [InlineData("DELETE FROM t WHERE x IN (SELECT id FROM u)", false)]
    [InlineData("UPDATE t SET a = CASE WHEN b = 1 THEN 1 END WHERE c = 2", false)]
    public void IsUpdateOrDeleteWithoutWhere_顶层判定(string sql, bool expected)
    {
        Assert.Equal(expected, _guard.IsUpdateOrDeleteWithoutWhere(sql));
    }

    // ---------- TRUNCATE 识别 ----------

    [Theory]
    [InlineData("TRUNCATE TABLE t", true)]
    [InlineData("truncate table t", true)]
    [InlineData("CREATE TABLE t (a INT)", false)]
    [InlineData("DELETE FROM t", false)]
    public void IsTruncateStatement_识别(string sql, bool expected)
    {
        Assert.Equal(expected, _guard.IsTruncateStatement(sql));
    }
}
