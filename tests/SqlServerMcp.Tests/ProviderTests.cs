using ModelContextProtocol;
using SqlServerMcp.Services;
using SqlServerMcp.Services.Providers;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>四类数据库 Provider 的方言适配测试（表名解析、标识符转义、目录 SQL 归一化）。</summary>
public class ProviderTests
{
    private readonly DatabaseProviderFactory _factory = new();

    [Theory]
    [InlineData("Users", "dbo", "Users")]
    [InlineData("dbo.Users", "dbo", "Users")]
    [InlineData(" sales.Orders ", "sales", "Orders")]
    public void SqlServerProvider_表名解析默认dbo(string table, string expectedSchema, string expectedTable)
    {
        var provider = new SqlServerProvider();
        var (schema, name) = provider.SplitTableName(table);
        Assert.Equal(expectedSchema, schema);
        Assert.Equal(expectedTable, name);
    }

    [Fact]
    public void SqlServerProvider_QuoteIdentifier_转义右方括号()
    {
        Assert.Equal("[a]]b]", new SqlServerProvider().QuoteIdentifier("a]b"));
    }

    [Fact]
    public void SqlServerProvider_目录SQL列名归一化()
    {
        var provider = new SqlServerProvider();
        Assert.Contains("approx_rows", provider.BuildListTablesSql());
        Assert.Contains("column_name", provider.BuildDescribeColumnsQuery("dbo", "t").Sql);
        Assert.Contains("pk_name", provider.BuildDescribePrimaryKeyQuery("dbo", "t").Sql);
        Assert.Contains("fk_name", provider.BuildDescribeForeignKeysQuery("dbo", "t").Sql);
        Assert.Contains("key_columns", provider.BuildListIndexesQuery(null, null).Sql);
        Assert.Contains("definition_head", provider.BuildListViewsSql());
        Assert.Contains("definition_head", provider.BuildListStoredProceduresSql());
    }

    [Fact]
    public void SqlServerProvider_分区表聚合与Max长度保留()
    {
        var provider = new SqlServerProvider();
        Assert.Contains("SUM(p.rows)", provider.BuildListTablesSql());
        Assert.Contains("c.max_length = -1", provider.BuildDescribeColumnsQuery("dbo", "t").Sql);
    }

    [Fact]
    public void MySqlProvider_表名解析_未指定库名为空schema()
    {
        var provider = new MySqlProvider();
        var (schema, name) = provider.SplitTableName("Users");
        Assert.Equal(string.Empty, schema);
        Assert.Equal("Users", name);

        var (schema2, name2) = provider.SplitTableName("mydb.Users");
        Assert.Equal("mydb", schema2);
        Assert.Equal("Users", name2);
    }

    [Fact]
    public void MySqlProvider_QuoteIdentifier_反引号翻倍()
    {
        Assert.Equal("`a``b`", new MySqlProvider().QuoteIdentifier("a`b"));
    }

    [Fact]
    public void MySqlProvider_目录SQL作用域正确()
    {
        var provider = new MySqlProvider();

        // 未指定库名 → DATABASE()
        var (sql1, p1) = provider.BuildDescribeColumnsQuery(string.Empty, "t");
        Assert.Contains("TABLE_SCHEMA = DATABASE()", sql1);
        Assert.Single(p1);

        // 指定库名 → @schema
        var (sql2, p2) = provider.BuildDescribeColumnsQuery("mydb", "t");
        Assert.Contains("TABLE_SCHEMA = @schema", sql2);
        Assert.Equal(2, p2.Length);

        // 索引查询：显式 schema 时按库过滤（I3 回归）
        var (sql3, p3) = provider.BuildListIndexesQuery("mydb", "t");
        Assert.Contains("TABLE_SCHEMA = @schema", sql3);
        Assert.Contains("TABLE_NAME = @table", sql3);
        Assert.Equal(2, p3.Length);

        var (sql4, _) = provider.BuildListIndexesQuery(null, "t");
        Assert.Contains("TABLE_SCHEMA = DATABASE()", sql4);
    }

    [Fact]
    public void PostgreSqlProvider_表名解析默认public()
    {
        var provider = new PostgreSqlProvider();
        var (schema, name) = provider.SplitTableName("Users");
        Assert.Equal("public", schema);
        Assert.Equal("Users", name);
    }

    [Fact]
    public void PostgreSqlProvider_QuoteIdentifier_双引号翻倍()
    {
        Assert.Equal("\"a\"\"b\"", new PostgreSqlProvider().QuoteIdentifier("a\"b"));
    }

    [Fact]
    public void PostgreSqlProvider_复合外键按序号对齐()
    {
        // I5 回归：必须使用 pg_constraint + unnest，而非 information_schema 双表连接
        var sql = new PostgreSqlProvider().BuildDescribeForeignKeysQuery("public", "t").Sql;
        Assert.Contains("pg_constraint", sql);
        Assert.Contains("unnest(con.conkey) WITH ORDINALITY", sql);
        Assert.Contains("f.ord = k.ord", sql);
        Assert.DoesNotContain("constraint_column_usage", sql);
    }

    [Fact]
    public void PostgreSqlProvider_函数体来自pg_proc()
    {
        // I6 回归：routine_definition 恒为 NULL，改用 pg_proc.prosrc
        var sql = new PostgreSqlProvider().BuildListStoredProceduresSql();
        Assert.Contains("pg_proc", sql);
        Assert.Contains("prosrc", sql);
    }

    [Fact]
    public void PostgreSqlProvider_索引查询始终按schema过滤()
    {
        // I4 回归：只按表名不过滤 schema 会跨 schema 返回同名表索引
        var provider = new PostgreSqlProvider();
        var (sql, parameters) = provider.BuildListIndexesQuery("public", "t");
        Assert.Contains("n.nspname = @schema", sql);
        Assert.Contains(parameters, p => p.ParameterName == "@schema");
    }

    [Fact]
    public void SqliteProvider_表名解析默认main()
    {
        var provider = new SqliteProvider();
        var (schema, name) = provider.SplitTableName("Users");
        Assert.Equal("main", schema);
        Assert.Equal("Users", name);
    }

    [Fact]
    public void SqliteProvider_PRAGMA表名按字面量转义()
    {
        var provider = new SqliteProvider();

        // 普通表名：字面量传入且无参数
        var (sql, parameters) = provider.BuildDescribeColumnsQuery("main", "Weather");
        Assert.Contains("pragma_table_info('Weather')", sql);
        Assert.Empty(parameters);

        // 含单引号表名：'' 翻倍转义，无法 break-out（注入防线）
        var (sql2, _) = provider.BuildDescribeColumnsQuery("main", "We'ather");
        Assert.Contains("pragma_table_info('We''ather')", sql2);

        // 外键查询：父表名同样按字面量注入
        var (fkSql, _) = provider.BuildDescribeForeignKeysQuery("main", "We'ather");
        Assert.Contains("'We''ather'", fkSql);
    }

    [Fact]
    public void SqliteProvider_视图用substr而非LEFT()
    {
        // 回归：SQLite 无 LEFT() 函数
        var sql = new SqliteProvider().BuildListViewsSql();
        Assert.Contains("substr(sql, 1, 500)", sql);
        Assert.DoesNotContain("LEFT(", sql);
    }

    [Fact]
    public void SqliteProvider_存储过程返回空结果集()
    {
        var sql = new SqliteProvider().BuildListStoredProceduresSql();
        Assert.Contains("WHERE 1 = 0", sql);
    }

    // ---------- 工厂 ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sqlserver")]
    [InlineData("  SqlServer  ")]
    public void Factory_缺省与大小写不敏感(string? type)
    {
        var provider = _factory.GetProvider(type);
        Assert.Equal("sqlserver", provider.ProviderId);
    }

    [Theory]
    [InlineData("mysql", "mysql")]
    [InlineData("PostgreSQL", "postgresql")]
    [InlineData("sqlite", "sqlite")]
    public void Factory_按类型路由(string type, string expectedId)
    {
        Assert.Equal(expectedId, _factory.GetProvider(type).ProviderId);
    }

    [Fact]
    public void Factory_未知类型拒绝并列出支持项()
    {
        var ex = Assert.Throws<McpException>(() => _factory.GetProvider("oracle"));
        Assert.Contains("oracle", ex.Message);
        Assert.Contains("sqlserver", ex.Message);
    }

    [Fact]
    public void Factory_四种类型均已注册()
    {
        Assert.Equal(4, _factory.SupportedTypes.Count);
    }

    [Fact]
    public void Phase2起四类库均支持写操作()
    {
        Assert.True(_factory.GetProvider("sqlserver").SupportsWrite);
        Assert.True(_factory.GetProvider("mysql").SupportsWrite);
        Assert.True(_factory.GetProvider("postgresql").SupportsWrite);
        Assert.True(_factory.GetProvider("sqlite").SupportsWrite);
    }

    [Theory]
    [InlineData("sqlserver", true)]
    [InlineData("mysql", false)]      // InnoDB DDL 隐式提交，不可演练
    [InlineData("postgresql", true)]
    [InlineData("sqlite", true)]
    public void SupportsTransactionalDdl_按方言区分(string providerId, bool expected)
    {
        Assert.Equal(expected, _factory.GetProvider(providerId).SupportsTransactionalDdl);
    }

    [Fact]
    public void MySqlProvider_附加黑名单覆盖账号与服务器级语句()
    {
        var extras = new MySqlProvider().ExtraDangerousKeywords;
        Assert.Contains("CREATE USER", extras);
        Assert.Contains("DROP USER", extras);
        Assert.Contains("SET GLOBAL", extras);
        Assert.Contains("FLUSH", extras);
        Assert.DoesNotContain("CREATE LOGIN", extras); // 全局黑名单已覆盖，不重复
    }

    [Fact]
    public void PostgreSqlProvider_附加黑名单覆盖角色与系统级语句()
    {
        var extras = new PostgreSqlProvider().ExtraDangerousKeywords;
        Assert.Contains("CREATE ROLE", extras);
        Assert.Contains("ALTER ROLE", extras);
        Assert.Contains("ALTER SYSTEM", extras);
    }

    [Fact]
    public void 存储过程支持位符合各方言能力()
    {
        // 工具层据此下发 supported:false，让调用方能区分「该方言不支持」与「确实没有对象」
        Assert.True(_factory.GetProvider("sqlserver").SupportsStoredProcedures);
        Assert.True(_factory.GetProvider("mysql").SupportsStoredProcedures);
        Assert.True(_factory.GetProvider("postgresql").SupportsStoredProcedures);
        Assert.False(_factory.GetProvider("sqlite").SupportsStoredProcedures);
    }

    [Fact]
    public void 四库均声明例程创建拦截规则()
    {
        // C2/CR-1：例程类语句改用正则规则（覆盖 CREATE OR ALTER / ALTER 等变体），
        // 端到端行为由 RoutineCreationPatternTests 验证
        foreach (var providerId in new[] { "sqlserver", "mysql", "postgresql", "sqlite" })
        {
            Assert.False(string.IsNullOrWhiteSpace(_factory.GetProvider(providerId).RoutineCreationPattern));
        }
    }

    [Theory]
    [InlineData("sqlserver", "@@VERSION")]
    [InlineData("mysql", "VERSION()")]
    [InlineData("postgresql", "server_version")]
    [InlineData("sqlite", "sqlite_version()")]
    public void BuildServerVersionSql_输出归一化列名(string providerId, string expectedFragment)
    {
        var sql = _factory.GetProvider(providerId).BuildServerVersionSql();
        Assert.Contains(expectedFragment, sql);
        Assert.Contains("server_version", sql);
        Assert.Contains("current_database", sql);
    }

    // ---------- 带引号标识符表名（M10） ----------

    [Theory]
    [InlineData("[my.table]", "dbo", "my.table")]        // 方括号包裹，点不切分
    [InlineData("dbo.[my.table]", "dbo", "my.table")]    // 混合形式
    [InlineData("[dbo].[my.table]", "dbo", "my.table")]
    public void SqlServerProvider_引号表名不再误切(string table, string expectedSchema, string expectedTable)
    {
        var provider = new SqlServerProvider();
        var (schema, name) = provider.SplitTableName(table);
        Assert.Equal(expectedSchema, schema);
        Assert.Equal(expectedTable, name);
    }

    [Theory]
    [InlineData("\"my.table\"", "public", "my.table")]
    [InlineData("public.\"my.table\"", "public", "my.table")]
    public void PostgreSqlProvider_引号表名不再误切(string table, string expectedSchema, string expectedTable)
    {
        var provider = new PostgreSqlProvider();
        var (schema, name) = provider.SplitTableName(table);
        Assert.Equal(expectedSchema, schema);
        Assert.Equal(expectedTable, name);
    }

    [Theory]
    [InlineData("`my.table`", "", "my.table")]
    [InlineData("mydb.`my.table`", "mydb", "my.table")]
    public void MySqlProvider_反引号表名不再误切(string table, string expectedSchema, string expectedTable)
    {
        var provider = new MySqlProvider();
        var (schema, name) = provider.SplitTableName(table);
        Assert.Equal(expectedSchema, schema);
        Assert.Equal(expectedTable, name);
    }

    [Theory]
    [InlineData("Weather")]
    [InlineData("main.Weather")]
    [InlineData("\"my.table\"")]
    public void SqliteProvider_引号与普通表名解析(string table)
    {
        var (schema, name) = new SqliteProvider().SplitTableName(table);
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Theory]
    [InlineData("a.b.c")]          // 三段裸写
    [InlineData("[unclosed")]      // 引号未闭合
    [InlineData("dbo.")]           // 空片段
    public void SplitTableName_非法表名拒绝(string table)
    {
        Assert.Throws<McpException>(() => new SqlServerProvider().SplitTableName(table));
    }
}
