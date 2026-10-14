using System.Data.Common;
using ModelContextProtocol;
using MySqlConnector;

namespace SqlServerMcp.Services.Providers;

/// <summary>MySQL 数据库提供程序（information_schema 目录、`` ` `` 标识符转义，只读）。</summary>
public sealed class MySqlProvider : IDatabaseProvider
{
    public string ProviderId => "mysql";

    public string DisplayName => "MySQL";

    public bool SupportsWrite => false;

    public DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

    public string QuoteIdentifier(string identifier) =>
        "`" + identifier.Replace("`", "``") + "`";

    /// <summary>MySQL 的 schema 即数据库名：仅传表名时用连接串的当前库（目录 SQL 中以 DATABASE() 表示）。</summary>
    public (string Schema, string Table) SplitTableName(string table)
    {
        if (string.IsNullOrWhiteSpace(table))
        {
            throw new McpException("表名不能为空。");
        }

        var parts = table.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            1 => (string.Empty, parts[0]),
            2 => (parts[0], parts[1]),
            _ => throw new McpException($"表名格式不正确：{table}。请使用 \"表名\" 或 \"库名.表名\"。")
        };
    }

    public string TranslateError(Exception ex, string database) => ex switch
    {
        MySqlException mySqlEx => mySqlEx.Number switch
        {
            1045 or 1044 or 1698 =>
                $"数据库「{database}」登录失败（MySQL 错误 {mySqlEx.Number}）。请检查连接字符串中的用户名/密码，以及该账户是否已授权访问此数据库。",
            1049 or 1046 =>
                $"数据库「{database}」不存在（MySQL 错误 {mySqlEx.Number}）。请检查连接字符串中的 Database 名称。",
            0 or 1042 or 2002 or 2003 or 2005 =>
                $"无法连接到「{database}」对应的 MySQL 实例（MySQL 错误 {mySqlEx.Number}）。请检查：" +
                "1) MySQL 服务是否已启动；2) 主机与端口（默认 3306）是否正确；3) 防火墙是否放行端口。",
            _ => $"数据库「{database}」操作失败（MySQL 错误 {mySqlEx.Number}）：{mySqlEx.Message}"
        },
        TimeoutException =>
            $"数据库「{database}」操作超时。请检查网络与服务器负载，或适当增大 CommandTimeoutSeconds。",
        _ => $"数据库「{database}」操作失败：{ex.Message}"
    };

    public string BuildListTablesSql() => """
        SELECT TABLE_SCHEMA AS `schema`, TABLE_NAME AS table_name, TABLE_ROWS AS approx_rows
        FROM information_schema.TABLES
        WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_SCHEMA = DATABASE()
        ORDER BY TABLE_SCHEMA, TABLE_NAME
        """;

    public string BuildListViewsSql() => """
        SELECT TABLE_SCHEMA AS `schema`, TABLE_NAME AS view_name,
               LEFT(VIEW_DEFINITION, 500) AS definition_head
        FROM information_schema.VIEWS
        WHERE TABLE_SCHEMA = DATABASE()
        ORDER BY TABLE_SCHEMA, TABLE_NAME
        """;

    public string BuildListStoredProceduresSql() => """
        SELECT ROUTINE_SCHEMA AS `schema`, ROUTINE_NAME AS proc_name,
               LEFT(ROUTINE_DEFINITION, 500) AS definition_head
        FROM information_schema.ROUTINES
        WHERE ROUTINE_TYPE = 'PROCEDURE' AND ROUTINE_SCHEMA = DATABASE()
        ORDER BY ROUTINE_SCHEMA, ROUTINE_NAME
        """;

    public (string Sql, DbParameter[] Parameters) BuildDescribeColumnsQuery(string schema, string table)
    {
        var (scope, parameters) = BuildScope(schema, table);
        return ($"""
            SELECT COLUMN_NAME AS column_name, DATA_TYPE AS data_type,
                   COALESCE(CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION) AS max_length,
                   NUMERIC_SCALE AS scale,
                   CASE WHEN IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS is_nullable,
                   COLUMN_DEFAULT AS default_value, COLUMN_KEY, EXTRA, COLUMN_COMMENT
            FROM information_schema.COLUMNS
            WHERE {scope}
            ORDER BY ORDINAL_POSITION
            """, parameters);
    }

    public (string Sql, DbParameter[] Parameters) BuildDescribePrimaryKeyQuery(string schema, string table)
    {
        var (scope, parameters) = BuildScope(schema, table);
        return ($"""
            SELECT CONSTRAINT_NAME AS pk_name, COLUMN_NAME AS column_name, ORDINAL_POSITION AS key_ordinal
            FROM information_schema.KEY_COLUMN_USAGE
            WHERE {scope} AND CONSTRAINT_NAME = 'PRIMARY'
            ORDER BY ORDINAL_POSITION
            """, parameters);
    }

    public (string Sql, DbParameter[] Parameters) BuildDescribeForeignKeysQuery(string schema, string table)
    {
        var (scope, parameters) = BuildScope(schema, table);
        return ($"""
            SELECT CONSTRAINT_NAME AS fk_name,
                   TABLE_SCHEMA AS parent_schema, TABLE_NAME AS parent_table, COLUMN_NAME AS parent_column,
                   REFERENCED_TABLE_SCHEMA AS ref_schema, REFERENCED_TABLE_NAME AS ref_table,
                   REFERENCED_COLUMN_NAME AS ref_column
            FROM information_schema.KEY_COLUMN_USAGE
            WHERE {scope} AND REFERENCED_TABLE_NAME IS NOT NULL
            ORDER BY CONSTRAINT_NAME, ORDINAL_POSITION
            """, parameters);
    }

    public (string Sql, DbParameter[] Parameters) BuildListIndexesQuery(string? schema, string? table)
    {
        // 未指定表名时列出当前库全部索引；MySQL 无包含列概念，included_columns 恒为 NULL。
        // schema 语义与 BuildScope 一致：显式指定库名时按库过滤，否则用 DATABASE() 当前库。
        var parameters = new List<DbParameter>();
        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(table))
        {
            if (string.IsNullOrWhiteSpace(schema))
            {
                conditions.Add("TABLE_SCHEMA = DATABASE()");
            }
            else
            {
                conditions.Add("TABLE_SCHEMA = @schema");
                parameters.Add(new MySqlParameter("@schema", schema));
            }

            conditions.Add("TABLE_NAME = @table");
            parameters.Add(new MySqlParameter("@table", table));
        }
        else
        {
            conditions.Add("TABLE_SCHEMA = DATABASE()");
        }

        var where = string.Join(" AND ", conditions);
        return ($"""
            SELECT TABLE_SCHEMA AS `schema`, TABLE_NAME AS table_name, INDEX_NAME AS index_name,
                   INDEX_TYPE AS type_desc,
                   CASE WHEN NON_UNIQUE = 0 THEN 1 ELSE 0 END AS is_unique,
                   CASE WHEN INDEX_NAME = 'PRIMARY' THEN 1 ELSE 0 END AS is_primary_key,
                   GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX SEPARATOR ', ') AS key_columns,
                   NULL AS included_columns
            FROM information_schema.STATISTICS
            WHERE {where}
            GROUP BY TABLE_SCHEMA, TABLE_NAME, INDEX_NAME, INDEX_TYPE, NON_UNIQUE
            ORDER BY TABLE_SCHEMA, TABLE_NAME, INDEX_NAME
            """, parameters.ToArray());
    }

    /// <summary>构建目录查询的 schema/table 作用域条件：未显式指定库名时用 DATABASE()（连接串当前库）。</summary>
    private static (string Scope, DbParameter[] Parameters) BuildScope(string schema, string table)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            return ("TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table",
                new DbParameter[] { new MySqlParameter("@table", table) });
        }

        return ("TABLE_SCHEMA = @schema AND TABLE_NAME = @table",
            new DbParameter[]
            {
                new MySqlParameter("@schema", schema),
                new MySqlParameter("@table", table)
            });
    }
}
