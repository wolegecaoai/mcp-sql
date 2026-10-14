using System.Data.Common;
using Microsoft.Data.SqlClient;
using ModelContextProtocol;

namespace SqlServerMcp.Services.Providers;

/// <summary>SQL Server 数据库提供程序（sys.* 目录视图、[] 标识符转义、dbo 默认 schema，支持写操作）。</summary>
public sealed class SqlServerProvider : IDatabaseProvider
{
    public string ProviderId => "sqlserver";

    public string DisplayName => "SQL Server";

    public bool SupportsWrite => true;

    public DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    public string QuoteIdentifier(string identifier) =>
        "[" + identifier.Replace("]", "]]") + "]";

    public (string Schema, string Table) SplitTableName(string table)
    {
        if (string.IsNullOrWhiteSpace(table))
        {
            throw new McpException("表名不能为空。");
        }

        var parts = table.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            1 => ("dbo", parts[0]),
            2 => (parts[0], parts[1]),
            _ => throw new McpException($"表名格式不正确：{table}。请使用 \"表名\" 或 \"schema.表名\"。")
        };
    }

    public string TranslateError(Exception ex, string database) => ex switch
    {
        SqlException sqlEx => sqlEx.Number switch
        {
            18456 or 18452 or 18470 =>
                $"数据库「{database}」登录失败（SQL Server 错误 {sqlEx.Number}）。" +
                "请检查连接字符串中的用户名/密码；使用 Windows 集成验证（Integrated Security=true）时，" +
                "请确认当前 Windows 账户已被授予该数据库的访问权限。",
            4060 or 40532 or 916 =>
                $"数据库「{database}」无法打开（错误 {sqlEx.Number}）：连接字符串中指定的数据库不存在，或当前账户无权访问。",
            53 or 40 or 10061 or 10060 or 258 =>
                $"无法连接到「{database}」对应的 SQL Server 实例（错误 {sqlEx.Number}）。请检查：" +
                "1) SQL Server 服务是否已启动；2) 是否启用了 TCP/IP 协议（SQL Server 配置管理器）；" +
                "3) 防火墙是否放行端口（默认 1433）。",
            -2 or -1 =>
                $"连接「{database}」超时（错误 {sqlEx.Number}）。请检查网络与服务器负载，或适当增大 CommandTimeoutSeconds。",
            _ => $"数据库「{database}」操作失败（SQL Server 错误 {sqlEx.Number}，第 {sqlEx.LineNumber} 行）：{sqlEx.Message}"
        },
        TimeoutException =>
            $"数据库「{database}」操作超时。请检查网络与服务器负载，或适当增大 CommandTimeoutSeconds。",
        _ => $"数据库「{database}」操作失败：{ex.Message}"
    };

    public string BuildListTablesSql() => """
        SELECT s.name AS [schema], t.name AS table_name, ISNULL(SUM(p.rows), 0) AS approx_rows
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        LEFT JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
        GROUP BY s.name, t.name
        ORDER BY s.name, t.name
        """;

    public string BuildListViewsSql() => """
        SELECT s.name AS [schema], v.name AS view_name,
               CAST(LEFT(m.definition, 500) AS nvarchar(500)) AS definition_head
        FROM sys.views v
        JOIN sys.schemas s ON s.schema_id = v.schema_id
        LEFT JOIN sys.sql_modules m ON m.object_id = v.object_id
        ORDER BY s.name, v.name
        """;

    public string BuildListStoredProceduresSql() => """
        SELECT s.name AS [schema], p.name AS proc_name,
               CAST(LEFT(m.definition, 500) AS nvarchar(500)) AS definition_head
        FROM sys.procedures p
        JOIN sys.schemas s ON s.schema_id = p.schema_id
        LEFT JOIN sys.sql_modules m ON m.object_id = p.object_id
        ORDER BY s.name, p.name
        """;

    public (string Sql, DbParameter[] Parameters) BuildDescribeColumnsQuery(string schema, string table) => ("""
        SELECT c.column_id, c.name AS column_name, tp.name AS data_type,
               CASE WHEN c.max_length = -1 THEN -1
                    WHEN tp.name IN ('nvarchar', 'nchar') THEN c.max_length / 2
                    ELSE c.max_length END AS max_length,
               c.precision, c.scale, c.is_nullable, c.is_identity,
               dc.definition AS default_value
        FROM sys.columns c
        JOIN sys.types tp ON tp.user_type_id = c.user_type_id
        JOIN sys.tables t ON t.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        OUTER APPLY (SELECT definition FROM sys.default_constraints WHERE object_id = c.default_object_id) dc
        WHERE s.name = @schema AND t.name = @table
        ORDER BY c.column_id
        """,
        new DbParameter[]
        {
            new SqlParameter("@schema", schema),
            new SqlParameter("@table", table)
        });

    public (string Sql, DbParameter[] Parameters) BuildDescribePrimaryKeyQuery(string schema, string table) => ("""
        SELECT kc.name AS pk_name, c.name AS column_name, ic.key_ordinal
        FROM sys.key_constraints kc
        JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        JOIN sys.tables t ON t.object_id = kc.parent_object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE s.name = @schema AND t.name = @table AND kc.type = 'PK'
        ORDER BY ic.key_ordinal
        """,
        new DbParameter[]
        {
            new SqlParameter("@schema", schema),
            new SqlParameter("@table", table)
        });

    public (string Sql, DbParameter[] Parameters) BuildDescribeForeignKeysQuery(string schema, string table) => ("""
        SELECT fk.name AS fk_name,
               sp.name AS parent_schema, tp.name AS parent_table, cp.name AS parent_column,
               sr.name AS ref_schema, tr.name AS ref_table, cr.name AS ref_column
        FROM sys.foreign_keys fk
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.tables tp ON tp.object_id = fk.parent_object_id
        JOIN sys.schemas sp ON sp.schema_id = tp.schema_id
        JOIN sys.columns cp ON cp.object_id = tp.object_id AND cp.column_id = fkc.parent_column_id
        JOIN sys.tables tr ON tr.object_id = fk.referenced_object_id
        JOIN sys.schemas sr ON sr.schema_id = tr.schema_id
        JOIN sys.columns cr ON cr.object_id = tr.object_id AND cr.column_id = fkc.referenced_column_id
        WHERE fk.parent_object_id IN (SELECT object_id FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = @schema AND t.name = @table)
        ORDER BY fk.name, fkc.constraint_column_id
        """,
        new DbParameter[]
        {
            new SqlParameter("@schema", schema),
            new SqlParameter("@table", table)
        });

    public (string Sql, DbParameter[] Parameters) BuildListIndexesQuery(string? schema, string? table)
    {
        const string sqlTemplate = """
            SELECT s.name AS [schema], t.name AS table_name, i.name AS index_name,
                   i.type_desc, i.is_unique, i.is_primary_key, i.is_unique_constraint,
                   STUFF((SELECT ', ' + c.name
                          FROM sys.index_columns ic
                          JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                          WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                          ORDER BY ic.key_ordinal
                          FOR XML PATH('')), 1, 2, '') AS key_columns,
                   STUFF((SELECT ', ' + c.name
                          FROM sys.index_columns ic
                          JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                          WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
                          ORDER BY ic.index_column_id
                          FOR XML PATH('')), 1, 2, '') AS included_columns
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE i.name IS NOT NULL {filter}
            ORDER BY s.name, t.name, i.name
            """;

        var parameters = new List<DbParameter>();
        var filter = string.Empty;
        if (!string.IsNullOrWhiteSpace(table))
        {
            filter = "AND s.name = @schema AND t.name = @table";
            parameters.Add(new SqlParameter("@schema", schema ?? "dbo"));
            parameters.Add(new SqlParameter("@table", table));
        }

        return (sqlTemplate.Replace("{filter}", filter), parameters.ToArray());
    }
}
