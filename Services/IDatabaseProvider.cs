using System.Data.Common;

namespace SqlServerMcp.Services;

/// <summary>
/// 数据库提供程序抽象：连接管理、目录查询、标识符转义、表名解析、错误翻译按数据库类型分化。
/// 实现类为无状态单例，由 <see cref="DatabaseProviderFactory"/> 创建并缓存。
/// </summary>
/// <remarks>
/// 目录查询输出列名归一化约定（各实现必须遵守，保证工具层与返回 JSON 结构跨数据库一致）：
/// <list type="bullet">
/// <item>ListTables → <c>schema, table_name, approx_rows</c></item>
/// <item>DescribeColumns → <c>column_name, data_type, max_length, is_nullable, default_value</c></item>
/// <item>DescribePrimaryKey → <c>pk_name, column_name, key_ordinal</c></item>
/// <item>DescribeForeignKeys → <c>fk_name, parent_schema, parent_table, parent_column, ref_schema, ref_table, ref_column</c></item>
/// <item>ListIndexes → <c>schema, table_name, index_name, type_desc, is_unique, is_primary_key, key_columns, included_columns</c></item>
/// <item>ListViews → <c>schema, view_name, definition_head</c></item>
/// <item>ListStoredProcedures → <c>schema, proc_name, definition_head</c></item>
/// </list>
/// </remarks>
public interface IDatabaseProvider
{
    /// <summary>提供程序标识（配置 Type 字段使用，小写）：sqlserver / mysql / postgresql / sqlite。</summary>
    string ProviderId { get; }

    /// <summary>中文显示名（错误提示与列表展示使用），如 "SQL Server"、"MySQL"。</summary>
    string DisplayName { get; }

    /// <summary>是否支持写操作（execute_non_query / execute_ddl）。Phase 1 仅 SQL Server 为 true，二期起四类库均为 true。</summary>
    bool SupportsWrite { get; }

    /// <summary>
    /// DDL 是否可事务化（决定 execute_ddl 演练模式是否可用）。
    /// MySQL InnoDB 的 DDL 会隐式提交、无法回滚，为 false；SQL Server / PostgreSQL / SQLite 为 true。
    /// </summary>
    bool SupportsTransactionalDdl { get; }

    /// <summary>
    /// 是否支持存储过程/函数目录查询。
    /// SQLite 无此概念（返回空结果集），工具层据此输出 <c>supported:false</c> 而非空数组，
    /// 让调用方能区分「该方言不支持」与「确实没有对象」。
    /// </summary>
    bool SupportsStoredProcedures { get; }

    /// <summary>该数据库类型的附加危险关键词（与全局黑名单合并后校验），无则返回空列表。</summary>
    IReadOnlyList<string> ExtraDangerousKeywords { get; }

    /// <summary>
    /// 例程/触发器/程序集等"语句体可藏匿危险词"的创建/修改语句的正则（CR-1），为 null 表示无此规则。
    /// 必须覆盖方言的全部语法变体（CREATE OR ALTER / CREATE OR REPLACE / ALTER / TEMP 等），
    /// 因为固定短语匹配会被修饰词绕过。
    /// </summary>
    string? RoutineCreationPattern { get; }

    /// <summary>服务器版本 / 当前库探测 SQL（输出列名归一化为 server_version, current_database）。</summary>
    string BuildServerVersionSql();

    /// <summary>按连接字符串创建连接对象（尚未打开，调用方负责打开与释放）。</summary>
    DbConnection CreateConnection(string connectionString);

    /// <summary>标识符转义（方言相关：SQL Server []、MySQL ``、PG/SQLite ""）。</summary>
    string QuoteIdentifier(string identifier);

    /// <summary>解析表名参数为 (schema, table)，缺省 schema 按方言填补（dbo / 当前库 / public / main）。</summary>
    (string Schema, string Table) SplitTableName(string table);

    /// <summary>将驱动异常翻译为中文可操作信息（含连接与查询两类场景）。</summary>
    string TranslateError(Exception ex, string database);

    /// <summary>列出所有用户表的 SQL（无参数）。</summary>
    string BuildListTablesSql();

    /// <summary>列出所有视图的 SQL（无参数）。</summary>
    string BuildListViewsSql();

    /// <summary>列出所有存储过程的 SQL（无参数）；不支持的数据库返回空结果集。</summary>
    string BuildListStoredProceduresSql();

    /// <summary>构建列信息查询（table 已解析）。</summary>
    (string Sql, DbParameter[] Parameters) BuildDescribeColumnsQuery(string schema, string table);

    /// <summary>构建主键查询（table 已解析）。</summary>
    (string Sql, DbParameter[] Parameters) BuildDescribePrimaryKeyQuery(string schema, string table);

    /// <summary>构建外键查询（table 已解析）。</summary>
    (string Sql, DbParameter[] Parameters) BuildDescribeForeignKeysQuery(string schema, string table);

    /// <summary>构建索引列表查询（table 为 null 时列出全部索引）。</summary>
    (string Sql, DbParameter[] Parameters) BuildListIndexesQuery(string? schema, string? table);
}
