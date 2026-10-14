using ModelContextProtocol;
using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// PostgreSQL 真实实例集成测试（需要环境变量 <c>SQLSERVERMCP_IT_POSTGRESQL</c>，见 docker-compose.test.yml）。
/// 覆盖：事务化 DDL 的演练回滚与提交、写全流程、守卫拦截、方言目录查询、错误翻译。
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlIntegrationTests : IDisposable
{
    private readonly IntegrationDatabase _db = new("SQLSERVERMCP_IT_POSTGRESQL", "postgresql", "PostgreSQL");

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task DDL演练_回滚后表不存在()
    {
        var table = IntegrationDatabase.UniqueTableName("mcp_ddl");

        // PostgreSQL 的 DDL 可事务化：演练模式在事务内执行后回滚
        await _db.Service.ExecuteInTransactionAsync(
            IntegrationDatabase.Alias, $"CREATE TABLE {table} (id int primary key)", commit: false, IntegrationDatabase.DdlTypes);

        Assert.False(await TableExistsAsync(table));
    }

    [Fact]
    public async Task DDL提交_表存在()
    {
        var table = IntegrationDatabase.UniqueTableName("mcp_ddl");
        try
        {
            await _db.Service.ExecuteInTransactionAsync(
                IntegrationDatabase.Alias, $"CREATE TABLE {table} (id int primary key)", commit: true, IntegrationDatabase.DdlTypes);

            Assert.True(await TableExistsAsync(table));
        }
        finally
        {
            await _db.TryDropTableAsync(table);
        }
    }

    [Fact]
    public async Task 写全流程_演练回滚无数据_确认提交有数据()
    {
        var table = IntegrationDatabase.UniqueTableName("mcp_write");
        try
        {
            await _db.Service.ExecuteInTransactionAsync(
                IntegrationDatabase.Alias, $"CREATE TABLE {table} (id int primary key, title varchar(50))", commit: true, IntegrationDatabase.DdlTypes);

            // 数据值用 ASCII：与 MySQL 侧保持一致，避免依赖目标库字符集
            var dryRun = await _db.Service.ExecuteInTransactionAsync(
                IntegrationDatabase.Alias, $"INSERT INTO {table} (id, title) VALUES (1, 'dry-run')", commit: false, IntegrationDatabase.DmlTypes);
            Assert.Equal(1, dryRun);
            Assert.Equal(0L, await CountRowsAsync(table));

            var commit = await _db.Service.ExecuteInTransactionAsync(
                IntegrationDatabase.Alias, $"INSERT INTO {table} (id, title) VALUES (2, 'commit')", commit: true, IntegrationDatabase.DmlTypes);
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
                IntegrationDatabase.Alias, $"CREATE TABLE {table} (id int primary key, name varchar(30) not null)", commit: true, IntegrationDatabase.DdlTypes);

            var provider = _db.Service.GetProvider(IntegrationDatabase.Alias);
            var tables = await _db.Service.ExecuteQueryAsync(IntegrationDatabase.Alias, provider.BuildListTablesSql(), maxRows: 10000);
            Assert.Contains(tables.Rows, row => string.Equals(row["table_name"]?.ToString(), table, StringComparison.OrdinalIgnoreCase));

            var (schema, name) = provider.SplitTableName(table);

            var (columnsSql, columnsParameters) = provider.BuildDescribeColumnsQuery(schema, name);
            var columns = await _db.Service.ExecuteQueryAsync(IntegrationDatabase.Alias, columnsSql, maxRows: 100, columnsParameters);
            Assert.Contains(columns.Rows, row => string.Equals(row["column_name"]?.ToString(), "id", StringComparison.OrdinalIgnoreCase));

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
    [InlineData("CREATE OR REPLACE FUNCTION f() RETURNS int AS $$ SELECT 1 $$ LANGUAGE sql", "例程")]
    [InlineData("CREATE EXTENSION IF NOT EXISTS plpython3u", "例程")]
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

        Assert.Matches("[一-龥]", ex.Message);
    }

    private async Task<bool> TableExistsAsync(string table)
    {
        var result = await _db.Service.ExecuteQueryAsync(
            IntegrationDatabase.Alias,
            "SELECT COUNT(*) AS c FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @name",
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

    private static System.Data.Common.DbParameter CreateParameter(string name, string value) =>
        new Npgsql.NpgsqlParameter(name, value);
}
