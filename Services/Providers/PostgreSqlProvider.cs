using System.Data.Common;
using ModelContextProtocol;
using Npgsql;

namespace SqlServerMcp.Services.Providers;

/// <summary>PostgreSQL 数据库提供程序（information_schema + pg_catalog 目录、"" 标识符转义、默认 public schema，只读）。</summary>
public sealed class PostgreSqlProvider : IDatabaseProvider
{
    public string ProviderId => "postgresql";

    public string DisplayName => "PostgreSQL";

    // Phase 2 起开放写能力；PG 的 DDL 可事务化，演练回滚可用
    public bool SupportsWrite => true;

    public bool SupportsTransactionalDdl => true;

    public bool SupportsStoredProcedures => true;

    // PG 角色与系统级设置语句（全局黑名单的 CREATE LOGIN 等不覆盖 PG 方言）
    public IReadOnlyList<string> ExtraDangerousKeywords { get; } = new[]
    {
        "CREATE ROLE",
        "DROP ROLE",
        "ALTER ROLE",
        "CREATE USER",
        "DROP USER",
        "ALTER SYSTEM"
    };

    // 例程/扩展/外部数据（C2）：函数体可藏危险词，LANGUAGE c 需 superuser 可提权；
    // 正则覆盖 CREATE / CREATE OR REPLACE / ALTER 变体
    public string? RoutineCreationPattern =>
        @"\b(CREATE(\s+OR\s+REPLACE)?|ALTER)\s+(FUNCTION|PROCEDURE|TRIGGER|EVENT|EXTENSION|SERVER|USER\s+MAPPING|FOREIGN\s+(DATA\s+WRAPPER|TABLE)|LANGUAGE|PUBLICATION|SUBSCRIPTION)\b";

    public string BuildServerVersionSql() => """
        SELECT current_setting('server_version') AS server_version, current_database() AS current_database
        """;

    public DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    public string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public (string Schema, string Table) SplitTableName(string table)
    {
        var (schema, name) = TableNameParser.Parse(table);
        return (schema ?? "public", name);
    }

    public string TranslateError(Exception ex, string database) => ex switch
    {
        NpgsqlException npgEx => npgEx.SqlState switch
        {
            "28P01" or "28000" or "28P02" =>
                $"数据库「{database}」登录失败（PostgreSQL 错误 {npgEx.SqlState}）。请检查连接字符串中的用户名/密码。",
            "3D000" =>
                $"数据库「{database}」不存在（PostgreSQL 错误 {npgEx.SqlState}）。请检查连接字符串中的 Database 名称。",
            "3F000" =>
                $"数据库「{database}」对应的 schema 不存在（PostgreSQL 错误 {npgEx.SqlState}）。",
            "08000" or "08001" or "08003" or "08004" or "08006" or "08007" or "08P01" =>
                $"无法连接到「{database}」对应的 PostgreSQL 实例（PostgreSQL 错误 {npgEx.SqlState}）。请检查：" +
                "1) PostgreSQL 服务是否已启动；2) 主机与端口（默认 5432）是否正确；3) 防火墙是否放行端口。",
            "53300" or "53400" =>
                $"数据库「{database}」连接数已满（PostgreSQL 错误 {npgEx.SqlState}）。请稍后重试或检查连接池配置。",
            "57P03" =>
                $"数据库「{database}」正在关闭，暂时无法连接（PostgreSQL 错误 {npgEx.SqlState}）。请稍后重试。",
            _ => $"数据库「{database}」操作失败（PostgreSQL 错误 {npgEx.SqlState}）：{npgEx.Message}"
        },
        TimeoutException =>
            $"数据库「{database}」操作超时。请检查网络与服务器负载，或适当增大 CommandTimeoutSeconds。",
        ArgumentException or FormatException or NotSupportedException =>
            $"数据库「{database}」连接字符串格式无效或不受支持，请检查 Type 与连接字符串是否匹配。",
        _ => $"数据库「{database}」操作失败：{ex.Message}"
    };

    public string BuildListTablesSql() => """
        SELECT t.table_schema AS "schema", t.table_name AS table_name,
               NULLIF((SELECT c.reltuples::bigint
                       FROM pg_class c
                       JOIN pg_namespace n ON n.oid = c.relnamespace
                       WHERE c.relname = t.table_name
                         AND n.nspname = t.table_schema
                         AND c.relkind IN ('r', 'p')), -1) AS approx_rows
        FROM information_schema.tables t
        WHERE t.table_type = 'BASE TABLE'
          AND t.table_schema NOT IN ('pg_catalog', 'information_schema')
          AND t.table_schema !~ '^pg_toast'
          AND t.table_schema !~ '^pg_temp'
        ORDER BY t.table_schema, t.table_name
        """;

    public string BuildListViewsSql() => """
        SELECT table_schema AS "schema", table_name AS view_name,
               substr(view_definition, 1, 500) AS definition_head
        FROM information_schema.views
        WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
          AND table_schema !~ '^pg_toast'
          AND table_schema !~ '^pg_temp'
        ORDER BY table_schema, table_name
        """;

    // information_schema.routines.routine_definition 恒为 NULL（不暴露函数体），
    // 改用 pg_proc.prosrc 取函数/存储过程定义前 500 字符（I6）
    public string BuildListStoredProceduresSql() => """
        SELECT ns.nspname AS "schema", p.proname AS proc_name,
               substr(p.prosrc, 1, 500) AS definition_head
        FROM pg_proc p
        JOIN pg_namespace ns ON ns.oid = p.pronamespace
        WHERE ns.nspname NOT IN ('pg_catalog', 'information_schema')
          AND ns.nspname !~ '^pg_toast'
          AND ns.nspname !~ '^pg_temp'
        ORDER BY ns.nspname, p.proname
        """;

    public (string Sql, DbParameter[] Parameters) BuildDescribeColumnsQuery(string schema, string table) => ("""
        SELECT column_name, data_type,
               COALESCE(character_maximum_length, datetime_precision) AS max_length,
               numeric_precision AS precision, numeric_scale AS scale,
               CASE WHEN is_nullable = 'YES' THEN 1 ELSE 0 END AS is_nullable,
               column_default AS default_value, ordinal_position
        FROM information_schema.columns
        WHERE table_schema = @schema AND table_name = @table
        ORDER BY ordinal_position
        """,
        new DbParameter[]
        {
            new NpgsqlParameter("@schema", schema),
            new NpgsqlParameter("@table", table)
        });

    public (string Sql, DbParameter[] Parameters) BuildDescribePrimaryKeyQuery(string schema, string table) => ("""
        SELECT tc.constraint_name AS pk_name, kcu.column_name, kcu.ordinal_position AS key_ordinal
        FROM information_schema.table_constraints tc
        JOIN information_schema.key_column_usage kcu
          ON kcu.constraint_name = tc.constraint_name
         AND kcu.table_schema = tc.table_schema
        WHERE tc.constraint_type = 'PRIMARY KEY'
          AND tc.table_schema = @schema AND tc.table_name = @table
        ORDER BY kcu.ordinal_position
        """,
        new DbParameter[]
        {
            new NpgsqlParameter("@schema", schema),
            new NpgsqlParameter("@table", table)
        });

    // 复合外键：information_schema 的两张用法表按约束名连接会产生 N×N 笛卡尔积且列配对无序，
    // 改用 pg_constraint + unnest(...) WITH ORDINALITY 按序号对齐本表列与被引用列（I5）
    public (string Sql, DbParameter[] Parameters) BuildDescribeForeignKeysQuery(string schema, string table) => ("""
        SELECT con.conname AS fk_name,
               ns.nspname AS parent_schema, cls.relname AS parent_table,
               att.attname AS parent_column,
               rns.nspname AS ref_schema, rcl.relname AS ref_table,
               ratt.attname AS ref_column
        FROM pg_constraint con
        JOIN pg_class cls ON cls.oid = con.conrelid
        JOIN pg_namespace ns ON ns.oid = cls.relnamespace
        JOIN pg_class rcl ON rcl.oid = con.confrelid
        JOIN pg_namespace rns ON rns.oid = rcl.relnamespace
        JOIN unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord) ON true
        JOIN pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = k.attnum
        JOIN unnest(con.confkey) WITH ORDINALITY AS f(attnum, ord) ON f.ord = k.ord
        JOIN pg_attribute ratt ON ratt.attrelid = con.confrelid AND ratt.attnum = f.attnum
        WHERE con.contype = 'f'
          AND ns.nspname = @schema AND cls.relname = @table
        ORDER BY con.conname, k.ord
        """,
        new DbParameter[]
        {
            new NpgsqlParameter("@schema", schema),
            new NpgsqlParameter("@table", table)
        });

    public (string Sql, DbParameter[] Parameters) BuildListIndexesQuery(string? schema, string? table)
    {
        // 始终按解析出的 schema 过滤（未指定时 SplitTableName 已默认 public），
        // 避免"只按表名"跨 schema 返回同名表索引（I4）
        var conditions = new List<string>
        {
            "n.nspname NOT IN ('pg_catalog', 'information_schema')",
            "n.nspname !~ '^pg_toast'"
        };
        var parameters = new List<DbParameter>();
        if (!string.IsNullOrWhiteSpace(table))
        {
            conditions.Add("t.relname = @table");
            parameters.Add(new NpgsqlParameter("@table", table));
            conditions.Add("n.nspname = @schema");
            parameters.Add(new NpgsqlParameter("@schema", string.IsNullOrWhiteSpace(schema) ? "public" : schema));
        }

        var where = string.Join(" AND ", conditions);
        return ($"""
            SELECT n.nspname AS "schema", t.relname AS table_name, i.relname AS index_name,
                   am.amname AS type_desc,
                   CASE WHEN ix.indisunique THEN 1 ELSE 0 END AS is_unique,
                   CASE WHEN ix.indisprimary THEN 1 ELSE 0 END AS is_primary_key,
                   (SELECT string_agg(a.attname, ', ' ORDER BY a.attnum)
                    FROM pg_attribute a
                    WHERE a.attrelid = ix.indexrelid
                      AND a.attnum > 0 AND a.attnum <= ix.indnkeyatts
                      AND NOT a.attisdropped) AS key_columns,
                   (SELECT string_agg(a.attname, ', ' ORDER BY a.attnum)
                    FROM pg_attribute a
                    WHERE a.attrelid = ix.indexrelid
                      AND a.attnum > ix.indnkeyatts
                      AND NOT a.attisdropped) AS included_columns
            FROM pg_index ix
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_class t ON t.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_am am ON am.oid = i.relam
            WHERE {where}
            ORDER BY n.nspname, t.relname, i.relname
            """, parameters.ToArray());
    }
}
