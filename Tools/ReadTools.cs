using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SqlServerMcp.Configuration;
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
    [Description("列出指定数据库的索引（可按表名过滤），含索引类型、是否唯一、键列与包含列。")]
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
        SqlGuardService sqlGuardService,
        IOptions<SqlServerOptions> options,
        [Description("目标数据库别名（必须是已授权库）")] string database,
        [Description("要执行的 SELECT 语句（仅单条）")] string sql,
        [Description("最大返回行数，默认 100，上限 5000")] int maxRows = 100,
        CancellationToken ct = default)
    {
        sqlGuardService.AssertReadOnly(sql, options.Value.DangerousKeywords);
        var limit = Math.Clamp(maxRows, 1, 5000);
        var result = await databaseService.ExecuteQueryAsync(database, sql, limit, ct: ct);
        return result.ToJson() + TruncationNotice(result, limit);
    }

    private static string TruncationNotice(QueryResult result, int limit) =>
        result.Truncated
            ? $"\n\n（注意：结果超过 {limit} 行已截断。如需完整数据，请缩小查询范围或分页查询。）"
            : string.Empty;
}
