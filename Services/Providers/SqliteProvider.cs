using System.Data.Common;
using ModelContextProtocol;
using Microsoft.Data.Sqlite;

namespace SqlServerMcp.Services.Providers;

/// <summary>
/// SQLite 数据库提供程序（sqlite_master + PRAGMA 表值函数目录、"" 标识符转义，只读）。
/// PRAGMA 表值函数不接受绑定参数，表名以单引号字面量（'' 转义）传入，杜绝注入。
/// </summary>
public sealed class SqliteProvider : IDatabaseProvider
{
    public string ProviderId => "sqlite";

    public string DisplayName => "SQLite";

    public bool SupportsWrite => false;

    public DbConnection CreateConnection(string connectionString) => new SqliteConnection(connectionString);

    public string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public (string Schema, string Table) SplitTableName(string table)
    {
        if (string.IsNullOrWhiteSpace(table))
        {
            throw new McpException("表名不能为空。");
        }

        var parts = table.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            1 => ("main", parts[0]),
            2 => (parts[0], parts[1]),
            _ => throw new McpException($"表名格式不正确：{table}。请使用 \"表名\" 或 \"库名.表名\"。")
        };
    }

    public string TranslateError(Exception ex, string database) => ex switch
    {
        SqliteException sqliteEx when sqliteEx.Message.Contains("unable to open", StringComparison.OrdinalIgnoreCase) =>
            $"无法打开数据库「{database}」的文件。请检查连接字符串中 Data Source 的路径是否存在、以及当前账户是否有读写权限。",
        SqliteException sqliteEx =>
            $"数据库「{database}」操作失败（SQLite 错误 {sqliteEx.SqliteErrorCode}）：{sqliteEx.Message}",
        TimeoutException =>
            $"数据库「{database}」操作超时。请检查磁盘与服务器负载，或适当增大 CommandTimeoutSeconds。",
        _ => $"数据库「{database}」操作失败：{ex.Message}"
    };

    public string BuildListTablesSql() => """
        SELECT 'main' AS "schema", name AS table_name, NULL AS approx_rows
        FROM sqlite_master
        WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
        ORDER BY name
        """;

    public string BuildListViewsSql() => """
        SELECT 'main' AS "schema", name AS view_name, substr(sql, 1, 500) AS definition_head
        FROM sqlite_master
        WHERE type = 'view' AND name NOT LIKE 'sqlite_%'
        ORDER BY name
        """;

    /// <summary>SQLite 无存储过程，返回空结果集（保持输出列名一致）。</summary>
    public string BuildListStoredProceduresSql() => """
        SELECT NULL AS "schema", NULL AS proc_name, NULL AS definition_head
        WHERE 1 = 0
        """;

    public (string Sql, DbParameter[] Parameters) BuildDescribeColumnsQuery(string schema, string table) => ($"""
        SELECT name AS column_name, type AS data_type, NULL AS max_length,
               CASE WHEN "notnull" = 0 THEN 1 ELSE 0 END AS is_nullable,
               dflt_value AS default_value, pk AS pk_ordinal
        FROM pragma_table_info({ToSqlLiteral(table)})
        ORDER BY cid
        """, Array.Empty<DbParameter>());

    public (string Sql, DbParameter[] Parameters) BuildDescribePrimaryKeyQuery(string schema, string table) => ($"""
        SELECT 'PRIMARY KEY' AS pk_name, name AS column_name, pk AS key_ordinal
        FROM pragma_table_info({ToSqlLiteral(table)})
        WHERE pk > 0
        ORDER BY pk
        """, Array.Empty<DbParameter>());

    public (string Sql, DbParameter[] Parameters) BuildDescribeForeignKeysQuery(string schema, string table) => ($"""
        SELECT 'fk_' || id AS fk_name,
               'main' AS parent_schema, {ToSqlLiteral(table)} AS parent_table,
               "from" AS parent_column,
               'main' AS ref_schema, "table" AS ref_table, "to" AS ref_column
        FROM pragma_foreign_key_list({ToSqlLiteral(table)})
        ORDER BY id, seq
        """, Array.Empty<DbParameter>());

    public (string Sql, DbParameter[] Parameters) BuildListIndexesQuery(string? schema, string? table)
    {
        if (string.IsNullOrWhiteSpace(table))
        {
            // 未指定表名：SQLite 无法跨表列举索引，返回空集合并保持列名一致
            return ("""
                SELECT NULL AS "schema", NULL AS table_name, NULL AS index_name,
                       NULL AS type_desc, NULL AS is_unique, NULL AS is_primary_key,
                       NULL AS key_columns, NULL AS included_columns
                WHERE 1 = 0
                """, Array.Empty<DbParameter>());
        }

        return ($"""
            SELECT 'main' AS "schema", {ToSqlLiteral(table)} AS table_name, name AS index_name,
                   CASE origin WHEN 'c' THEN 'CREATE INDEX' WHEN 'u' THEN 'UNIQUE' WHEN 'pk' THEN 'PRIMARY KEY' ELSE origin END AS type_desc,
                   CASE WHEN "unique" = 1 THEN 1 ELSE 0 END AS is_unique,
                   CASE WHEN origin = 'pk' THEN 1 ELSE 0 END AS is_primary_key,
                   NULL AS key_columns, NULL AS included_columns
            FROM pragma_index_list({ToSqlLiteral(table)})
            ORDER BY name
            """, Array.Empty<DbParameter>());
    }

    /// <summary>将表名转为 SQL 字符串字面量（单引号翻倍转义），供 PRAGMA 表值函数使用。</summary>
    private static string ToSqlLiteral(string value) => "'" + value.Replace("'", "''") + "'";
}
