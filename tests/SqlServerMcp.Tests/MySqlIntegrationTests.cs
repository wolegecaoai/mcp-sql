using ModelContextProtocol;
using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// MySQL 真实实例集成测试（需要环境变量 <c>SQLSERVERMCP_IT_MYSQL</c>，见 docker-compose.test.yml）。
/// 覆盖：DDL 不可事务化的 fail-closed 行为、写全流程演练/提交、守卫拦截、方言目录查询、错误翻译。
/// </summary>
[Trait("Category", "Integration")]
public sealed class MySqlIntegrationTests : IDisposable
{
    private readonly IntegrationDatabase _db = new("SQLSERVERMCP_IT_MYSQL", "mysql", "MySQL");

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task DDL演练模式被拒绝且不建立连接即可返回()
    {
        var table = IntegrationDatabase.UniqueTableName("mcp_ddl");

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            _db.Service.ExecuteInTransactionAsync(
                IntegrationDatabase.Alias, $"CREATE TABLE {table} (Id INT PRIMARY KEY)", commit: false, IntegrationDatabase.DdlTypes));

        // MySQL 的 DDL 隐式提交不可回滚：演练模式必须 fail-closed 拒绝，而不是"执行后假装回滚"
        Assert.Contains("无法回滚", ex.Message);
        Assert.False(await TableExistsAsync(table));
    }

    [Fact]
    public async Task 写全流程_演练回滚无数据_确认提交有数据()
    {
        var table = IntegrationDatabase.UniqueTableName("mcp_write");
        try
        {
            await _db.Service.ExecuteInTransactionAsync(
                IntegrationDatabase.Alias, $"CREATE TABLE {table} (Id INT PRIMARY KEY, Title VARCHAR(50))", commit: true, IntegrationDatabase.DdlTypes);

            // 数据值刻意用 ASCII：目标库字符集可能是 latin1（MySQL 5.7 默认），
            // 写入中文会触发 1366 Incorrect string value，与本次要验证的事务语义无关
            var dryRun = await _db.Service.ExecuteInTransactionAsync(
                IntegrationDatabase.Alias, $"INSERT INTO {table} (Id, Title) VALUES (1, 'dry-run')", commit: false, IntegrationDatabase.DmlTypes);
            Assert.Equal(1, dryRun);
            Assert.Equal(0L, await CountRowsAsync(table));

            // 提交：真正落库
            var commit = await _db.Service.ExecuteInTransactionAsync(
                IntegrationDatabase.Alias, $"INSERT INTO {table} (Id, Title) VALUES (2, 'commit')", commit: true, IntegrationDatabase.DmlTypes);
            Assert.Equal(1, commit);
            Assert.Equal(1L, await CountRowsAsync(table));
        }
        finally
        {
            await _db.TryDropTableAsync(table);
        }
    }

    [Fact]
    public async Task 目录查询_能查到新建表及其结构()
    {
        var table = IntegrationDatabase.UniqueTableName("mcp_catalog");
        try
        {
            await _db.Service.ExecuteInTransactionAsync(
                IntegrationDatabase.Alias, $"CREATE TABLE {table} (Id INT PRIMARY KEY, Name VARCHAR(30) NOT NULL)", commit: true, IntegrationDatabase.DdlTypes);

            // list_tables：验证方言 SQL 可执行且列别名契约（table_name）成立
            var provider = _db.Service.GetProvider(IntegrationDatabase.Alias);
            var tables = await _db.Service.ExecuteQueryAsync(IntegrationDatabase.Alias, provider.BuildListTablesSql(), maxRows: 10000);
            Assert.Contains(tables.Rows, row => string.Equals(row["table_name"]?.ToString(), table, StringComparison.OrdinalIgnoreCase));

            var (schema, name) = provider.SplitTableName(table);

            var (columnsSql, columnsParameters) = provider.BuildDescribeColumnsQuery(schema, name);
            var columns = await _db.Service.ExecuteQueryAsync(IntegrationDatabase.Alias, columnsSql, maxRows: 100, columnsParameters);
            Assert.Contains(columns.Rows, row => string.Equals(row["column_name"]?.ToString(), "Id", StringComparison.OrdinalIgnoreCase));

            var (pkSql, pkParameters) = provider.BuildDescribePrimaryKeyQuery(schema, name);
            var primaryKey = await _db.Service.ExecuteQueryAsync(IntegrationDatabase.Alias, pkSql, maxRows: 100, pkParameters);
            Assert.NotEmpty(primaryKey.Rows);
        }
        finally
        {
            await _db.TryDropTableAsync(table);
        }
    }

    [Theory]
    [InlineData("CREATE PROCEDURE p() BEGIN SELECT 1 END", "例程")]
    [InlineData("CREATE OR REPLACE FUNCTION f() RETURNS INT RETURN 1", "例程")]
    [InlineData("DROP DATABASE some_db", "危险关键词")]
    [InlineData("SELECT 1; SELECT 2", "只允许执行一条")]
    public void 守卫拦截_拒绝原因可读(string sql, string expectedReason)
    {
        var ex = Assert.Throws<McpException>(() =>
            _db.Service.ValidateWriteStatement(
                IntegrationDatabase.Alias,
                sql,
                new[] { SqlStatementType.Insert, SqlStatementType.Update, SqlStatementType.Delete, SqlStatementType.Ddl },
                "execute_ddl"));

        Assert.Contains(expectedReason, ex.Message);
    }

    [Fact]
    public async Task 错误翻译_驱动异常转为中文提示()
    {
        var missingTable = IntegrationDatabase.UniqueTableName("mcp_missing");

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            _db.Service.ExecuteReadOnlyQueryAsync(
                IntegrationDatabase.Alias, $"SELECT * FROM {missingTable}", maxRows: 10));

        // 断言「已翻译为中文」而非具体措辞（各 Provider 文案不同但都必须含中文）
        Assert.Matches("[一-龥]", ex.Message);
    }

    private async Task<bool> TableExistsAsync(string table)
    {
        var result = await _db.Service.ExecuteQueryAsync(
            IntegrationDatabase.Alias,
            "SELECT COUNT(*) AS c FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @name",
            maxRows: 1,
            new[] { CreateParameter("@name", table) });

        return Convert.ToInt64(result.Rows[0]["c"]) > 0;
    }

    private async Task<long> CountRowsAsync(string table)
    {
        var result = await _db.Service.ExecuteQueryAsync(
            IntegrationDatabase.Alias, $"SELECT COUNT(*) AS c FROM {table}", maxRows: 1);

        return Convert.ToInt64(result.Rows[0]["c"]);
    }

    private static System.Data.Common.DbParameter CreateParameter(string name, string value)
    {
        var parameter = new MySqlConnector.MySqlParameter(name, value);
        return parameter;
    }
}
