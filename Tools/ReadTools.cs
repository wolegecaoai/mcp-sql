using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SqlServerMcp.Services;

namespace SqlServerMcp.Tools;

/// <summary>只读 MCP 工具集：数据库/表/结构探查与 SELECT 查询（目录 SQL 由各数据库 Provider 提供）。</summary>
[McpServerToolType]
public class ReadTools
{
    [McpServerTool(Name = "list_databases", ReadOnly = true, Idempotent = true)]
    [Description("列出本 MCP 服务器已授权的所有数据库别名、类型及说明。调用其他工具前先确认目标数据库别名。")]
    public static string ListDatabases(DatabaseService databaseService)
    {
        var databases = databaseService.GetDatabaseList();
        if (databases.Count == 0)
        {
            return "尚未配置任何授权数据库。请在 appsettings.json 的 SqlServer:Databases 节中添加。";
        }

        var lines = databases.Select(item =>
        {
            var description = string.IsNullOrWhiteSpace(item.Description) ? "" : $"：{item.Description}";
            return $"- {item.Name}（{item.Type}）{description}";
        });

        return "已授权数据库：\n" + string.Join("\n", lines);
    }

    [McpServerTool(Name = "list_tables", ReadOnly = true, Idempotent = true)]
    [Description("列出指定数据库中的所有用户表（schema、表名、近似行数）。")]
    public static async Task<string> ListTablesAsync(
        DatabaseService databaseService,
        [Description("目标数据库别名（必须是已授权库，可通过 list_databases 查看）")] string database)
    {
        var provider = databaseService.GetProvider(database);
        var sql = provider.BuildListTablesSql();

        var result = await databaseService.ExecuteQueryAsync(database, sql, maxRows: 10000);
        return result.ToJson() + TruncationNotice(result, 10000);
    }

    [McpServerTool(Name = "describe_table", ReadOnly = true, Idempotent = true)]
    [Description("查看指定表的详细结构：列（数据类型/长度/可空/默认值）、主键列、外键引用关系。")]
    public static async Task<string> DescribeTableAsync(
        DatabaseService databaseService,
        [Description("目标数据库别名（必须是已授权库）")] string database,
        [Description("表名，格式为 \"表名\" 或 \"schema.表名\"，例如 dbo.Users")] string table)
    {
        var provider = databaseService.GetProvider(database);
        var (schema, tableName) = provider.SplitTableName(table);

        var (columnsSql, columnsParameters) = provider.BuildDescribeColumnsQuery(schema, tableName);
        var columns = await databaseService.ExecuteQueryAsync(database, columnsSql, maxRows: 1000, columnsParameters);
        if (columns.Rows.Count == 0)
        {
            throw new McpException($"数据库「{database}」中不存在表 {tableName}（或当前账户无权查看其结构）。");
        }

        var (primaryKeySql, primaryKeyParameters) = provider.BuildDescribePrimaryKeyQuery(schema, tableName);
        var primaryKey = await databaseService.ExecuteQueryAsync(database, primaryKeySql, maxRows: 100, primaryKeyParameters);

        var (foreignKeySql, foreignKeyParameters) = provider.BuildDescribeForeignKeysQuery(schema, tableName);
        var foreignKeys = await databaseService.ExecuteQueryAsync(database, foreignKeySql, maxRows: 1000, foreignKeyParameters);

        var payload = new
        {
            table = tableName,
            columns = columns.Rows,
            primary_key = primaryKey.Rows,
            foreign_keys = foreignKeys.Rows
        };

        return JsonSerializer.Serialize(payload, DatabaseService.SerializerOptions);
    }

    [McpServerTool(Name = "list_indexes", ReadOnly = true, Idempotent = true)]
    [Description("列出指定数据库的索引（可按表名过滤），含索引类型、是否唯一、键列与包含列。注意：MySQL 的键列拼接受 group_concat_max_len（默认 1024 字符）限制，超长索引列表可能被截断。")]
    public static async Task<string> ListIndexesAsync(
        DatabaseService databaseService,
        [Description("目标数据库别名（必须是已授权库）")] string database,
        [Description("可选：仅查看某张表的索引，格式为 \"表名\" 或 \"schema.表名\"")] string? table = null)
    {
        var provider = databaseService.GetProvider(database);

        string? schema = null;
        string? tableName = null;
        if (!string.IsNullOrWhiteSpace(table))
        {
            (schema, tableName) = provider.SplitTableName(table);
        }

        var (sql, parameters) = provider.BuildListIndexesQuery(schema, tableName);
        var result = await databaseService.ExecuteQueryAsync(database, sql, maxRows: 5000, parameters);
        return result.ToJson() + TruncationNotice(result, 5000);
    }

    [McpServerTool(Name = "list_views", ReadOnly = true, Idempotent = true)]
    [Description("列出指定数据库中的所有视图（含定义前 500 字符）。")]
    public static async Task<string> ListViewsAsync(
        DatabaseService databaseService,
        [Description("目标数据库别名（必须是已授权库）")] string database)
    {
        var provider = databaseService.GetProvider(database);
        var sql = provider.BuildListViewsSql();

        var result = await databaseService.ExecuteQueryAsync(database, sql, maxRows: 5000);
        return result.ToJson() + TruncationNotice(result, 5000);
    }

    [McpServerTool(Name = "list_stored_procedures", ReadOnly = true, Idempotent = true)]
    [Description("列出指定数据库中的所有存储过程（含定义前 500 字符；SQLite 等不支持的数据库返回空列表）。")]
    public static async Task<string> ListStoredProceduresAsync(
        DatabaseService databaseService,
        [Description("目标数据库别名（必须是已授权库）")] string database)
    {
        var provider = databaseService.GetProvider(database);
        var sql = provider.BuildListStoredProceduresSql();

        var result = await databaseService.ExecuteQueryAsync(database, sql, maxRows: 5000);
        return result.ToJson() + TruncationNotice(result, 5000);
    }

    [McpServerTool(Name = "execute_query", ReadOnly = true, Idempotent = true)]
    [Description("在指定数据库上执行只读 SELECT 查询（支持 WITH CTE）。仅允许单条查询语句，返回 JSON 数组结果。")]
    public static async Task<string> ExecuteQueryAsync(
        DatabaseService databaseService,
        AuditLogger auditLogger,
        [Description("目标数据库别名（必须是已授权库）")] string database,
        [Description("要执行的 SELECT 语句（仅单条）")] string sql,
        [Description("最大返回行数，默认 100，上限 5000")] int maxRows = 100,
        CancellationToken ct = default)
    {
        // 只读守卫已下沉至 DatabaseService.ExecuteReadOnlyQueryAsync（M12）：单语句/分类/INTO/黑名单自动生效
        var limit = Math.Clamp(maxRows, 1, 5000);
        var result = await ExecuteGuardedReadAsync(databaseService, auditLogger, database, "execute_query", sql, limit, ct);
        return result.ToJson() + TruncationNotice(result, limit);
    }

    [McpServerTool(Name = "test_connection", ReadOnly = true, Idempotent = true)]
    [Description("测试指定数据库的连通性，返回 Provider 类型、服务器版本、当前库名与连接耗时（毫秒）。")]
    public static async Task<string> TestConnectionAsync(
        DatabaseService databaseService,
        [Description("目标数据库别名（必须是已授权库，可通过 list_databases 查看）")] string database,
        CancellationToken ct = default)
    {
        var provider = databaseService.GetProvider(database);

        var startedAt = System.Diagnostics.Stopwatch.StartNew();
        var result = await databaseService.ExecuteQueryAsync(database, provider.BuildServerVersionSql(), maxRows: 1, ct: ct);
        startedAt.Stop();

        var row = result.Rows.Count > 0 ? result.Rows[0] : new Dictionary<string, object?>();
        var payload = new
        {
            database,
            provider = provider.ProviderId,
            display_name = provider.DisplayName,
            connected = true,
            latency_ms = startedAt.ElapsedMilliseconds,
            server_version = row.TryGetValue("server_version", out var version) ? version : null,
            current_database = row.TryGetValue("current_database", out var current) ? current : null
        };

        return JsonSerializer.Serialize(payload, DatabaseService.SerializerOptions);
    }

    [McpServerTool(Name = "export_query_result", ReadOnly = true, Idempotent = true)]
    [Description("执行只读 SELECT 查询（支持 WITH CTE）并将结果导出为指定格式。仅允许单条查询语句，只读守卫与黑名单自动生效。")]
    public static async Task<string> ExportQueryResultAsync(
        DatabaseService databaseService,
        AuditLogger auditLogger,
        [Description("目标数据库别名（必须是已授权库）")] string database,
        [Description("要执行的 SELECT 语句（仅单条）")] string sql,
        [Description("导出格式：csv（默认）/ markdown / json")] string format = "csv",
        [Description("最大导出行数，默认 100，上限 5000")] int maxRows = 100,
        CancellationToken ct = default)
    {
        var limit = Math.Clamp(maxRows, 1, 5000);
        var result = await ExecuteGuardedReadAsync(databaseService, auditLogger, database, "export_query_result", sql, limit, ct);

        var normalizedFormat = format.Trim().ToLowerInvariant();
        var content = normalizedFormat switch
        {
            "csv" => ExportFormatter.ToCsv(result),
            "markdown" or "md" => ExportFormatter.ToMarkdown(result),
            "json" => ExportFormatter.ToJson(result),
            _ => throw new McpException($"不支持的导出格式「{format}」。支持的格式：csv、markdown、json。")
        };

        // CSV/JSON 保持机器可读纯净（截断提示会污染数据本身），提示仅附加于 Markdown（M3）
        var notice = normalizedFormat is "markdown" or "md" ? TruncationNotice(result, limit) : string.Empty;
        return content + notice;
    }

    /// <summary>
    /// 先做只读守卫校验（拒绝则记审计后抛出），再执行查询。
    /// 守卫与执行分离是为了避免把连接/查询失败误标为"守卫拒绝"（MI-2）。
    /// </summary>
    private static async Task<QueryResult> ExecuteGuardedReadAsync(
        DatabaseService databaseService,
        AuditLogger auditLogger,
        string database,
        string tool,
        string sql,
        int limit,
        CancellationToken ct)
    {
        try
        {
            databaseService.AssertReadOnlyQuery(database, sql, tool);
        }
        catch (McpException ex)
        {
            auditLogger.LogGuardRejected(database, tool, ex.Message, sql);
            throw;
        }

        return await databaseService.ExecuteQueryAsync(database, sql, limit, ct: ct);
    }

    private static string TruncationNotice(QueryResult result, int limit) =>
        result.Truncated
            ? $"\n\n（注意：结果超过 {limit} 行已截断。如需完整数据，请缩小查询范围或分页查询。）"
            : string.Empty;
}
