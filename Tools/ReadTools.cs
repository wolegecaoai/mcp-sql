using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SqlServerMcp.Services;

namespace SqlServerMcp.Tools;

/// <summary>
/// 只读 MCP 工具集：数据库/表/结构探查、SELECT 查询与导出。
/// <para>
/// 输出契约（对 AI 调用方友好）：查询与目录类工具默认返回**信封**（见 <see cref="ResponseFormatter"/>），
/// 输出恒为合法 JSON——元数据（database/columns/row_count/truncated）与提示（notice/next）都在 JSON 内，
/// 不再有「JSON 之后追加中文散文」这种无法解析的形态。
/// </para>
/// </summary>
[McpServerToolType]
public class ReadTools
{
    /// <summary>
    /// 批量探测单库服务器信息的超时（秒）：刻意短于 CommandTimeoutSeconds，
    /// 否则多个不可达库会把 list_databases 拖到几分钟。
    /// </summary>
    private const int ServerInfoProbeTimeoutSeconds = 5;

    [McpServerTool(Name = "list_databases", ReadOnly = true, Idempotent = true)]
    [Description("列出已授权的数据库别名、类型与说明；并标注该类型是否支持 DDL 演练。可选探测各库版本与当前库名（并行探测、单库最多 5 秒，失败只影响该条目）。调用其他工具前先确认目标库别名。")]
    public static async Task<string> ListDatabasesAsync(
        DatabaseService databaseService,
        [Description("是否探测各库服务器版本与当前库名，默认 false；探测为并行且单库限时 5 秒，失败只标记该条目")] bool includeServerInfo = false,
        CancellationToken ct = default)
    {
        var databases = databaseService.GetDatabaseList();
        if (databases.Count == 0)
        {
            // 空态也用信封：保持「输出恒为合法 JSON」的契约
            return JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["count"] = 0,
                ["databases"] = Array.Empty<object>(),
                ["notice"] = "尚未配置任何授权数据库。请在 appsettings.json 的 SqlServer:Databases 节中添加。"
            }, DatabaseService.SerializerOptions);
        }

        var items = new List<Dictionary<string, object?>>();
        foreach (var (name, type, description) in databases)
        {
            var item = new Dictionary<string, object?>
            {
                ["database"] = name,
                ["type"] = type,
                ["description"] = description
            };

            try
            {
                // 注意：必须按「类型」取 Provider；按别名取会因 "sqlite" 不是别名而抛错
                var provider = databaseService.GetProviderByType(type);
                item["display_name"] = provider.DisplayName;
                item["ddl_rehearsal"] = provider.SupportsTransactionalDdl;
                item["supports_stored_procedures"] = provider.SupportsStoredProcedures;
            }
            catch (McpException ex)
            {
                // 单个条目类型非法不应让整个发现调用失败
                item["type_error"] = ex.Message;
            }

            items.Add(item);
        }

        if (includeServerInfo)
        {
            var probeResults = await databaseService.GetAllServerInfoAsync(ServerInfoProbeTimeoutSeconds, ct);
            foreach (var (database, info, error) in probeResults)
            {
                var item = items.FirstOrDefault(entry =>
                    string.Equals(entry["database"]?.ToString(), database, StringComparison.OrdinalIgnoreCase));
                if (item is null)
                {
                    continue;
                }

                if (info is not null)
                {
                    item["connected"] = true;
                    item["server_version"] = info.ServerVersion;
                    item["current_database"] = info.CurrentDatabase;
                    item["latency_ms"] = info.LatencyMs;
                }
                else
                {
                    item["connected"] = false;
                    item["error"] = error;
                }
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["count"] = items.Count,
            ["include_server_info"] = includeServerInfo,
            ["databases"] = items
        };

        return JsonSerializer.Serialize(payload, DatabaseService.SerializerOptions);
    }

    [McpServerTool(Name = "list_tables", ReadOnly = true, Idempotent = true)]
    [Description("列出指定数据库的用户表（schema、表名、近似行数）。")]
    public static async Task<string> ListTablesAsync(
        DatabaseService databaseService,
        [Description("目标数据库别名（必须是已授权库，可通过 list_databases 查看）")] string database,
        [Description("跳过结果前 N 行（分页续取用），默认 0")] int offset = 0,
        [Description("响应格式：envelope（默认，含元数据与提示）/ compact（列名只出现一次，更省 token）/ rows（历史裸数组）")] string responseFormat = ResponseFormatter.Envelope)
    {
        var provider = databaseService.GetProvider(database);
        var result = await databaseService.ExecuteQueryAsync(
            database, provider.BuildListTablesSql(), maxRows: 10000, offset: Math.Max(0, offset));

        return ResponseFormatter.Format(result, database, "list_tables", responseFormat, 10000, limitAdjustable: false);
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

        // 带 database/schema 等自我描述字段：多轮对话中调用方无需自己记住上下文
        var payload = new Dictionary<string, object?>
        {
            ["database"] = database,
            ["schema"] = schema,
            ["table"] = tableName,
            ["column_count"] = columns.Rows.Count,
            ["columns"] = columns.Rows,
            ["primary_key"] = primaryKey.Rows,
            ["foreign_keys"] = foreignKeys.Rows
        };

        return JsonSerializer.Serialize(payload, DatabaseService.SerializerOptions);
    }

    [McpServerTool(Name = "list_indexes", ReadOnly = true, Idempotent = true)]
    [Description("列出指定数据库的索引（可按表名过滤），含索引类型、是否唯一、键列与包含列。")]
    public static async Task<string> ListIndexesAsync(
        DatabaseService databaseService,
        [Description("目标数据库别名（必须是已授权库）")] string database,
        [Description("可选：仅查看某张表的索引，格式为 \"表名\" 或 \"schema.表名\"")] string? table = null,
        [Description("跳过结果前 N 行（分页续取用），默认 0")] int offset = 0,
        [Description("响应格式：envelope（默认）/ compact / rows（历史裸数组）")] string responseFormat = ResponseFormatter.Envelope)
    {
        var provider = databaseService.GetProvider(database);

        string? schema = null;
        string? tableName = null;
        if (!string.IsNullOrWhiteSpace(table))
        {
            (schema, tableName) = provider.SplitTableName(table);
        }

        var (sql, parameters) = provider.BuildListIndexesQuery(schema, tableName);
        var result = await databaseService.ExecuteQueryAsync(
            database, sql, maxRows: 5000, parameters, offset: Math.Max(0, offset));

        return ResponseFormatter.Format(result, database, "list_indexes", responseFormat, 5000, limitAdjustable: false);
    }

    [McpServerTool(Name = "list_views", ReadOnly = true, Idempotent = true)]
    [Description("列出指定数据库的所有视图（含定义前 500 字符）。")]
    public static async Task<string> ListViewsAsync(
        DatabaseService databaseService,
        [Description("目标数据库别名（必须是已授权库）")] string database,
        [Description("跳过结果前 N 行（分页续取用），默认 0")] int offset = 0,
        [Description("响应格式：envelope（默认）/ compact / rows（历史裸数组）")] string responseFormat = ResponseFormatter.Envelope)
    {
        var provider = databaseService.GetProvider(database);
        var result = await databaseService.ExecuteQueryAsync(
            database, provider.BuildListViewsSql(), maxRows: 5000, offset: Math.Max(0, offset));

        return ResponseFormatter.Format(result, database, "list_views", responseFormat, 5000, limitAdjustable: false);
    }

    [McpServerTool(Name = "list_stored_procedures", ReadOnly = true, Idempotent = true)]
    [Description("列出指定数据库的所有存储过程/函数（含定义前 500 字符）。SQLite 无此概念，会返回 supported=false 的说明。")]
    public static async Task<string> ListStoredProceduresAsync(
        DatabaseService databaseService,
        [Description("目标数据库别名（必须是已授权库）")] string database,
        [Description("跳过结果前 N 行（分页续取用），默认 0")] int offset = 0,
        [Description("响应格式：envelope（默认）/ compact / rows（历史裸数组）")] string responseFormat = ResponseFormatter.Envelope)
    {
        var provider = databaseService.GetProvider(database);

        // 方言不支持时直接返回说明，不建立连接——否则会暴露出与「查询不适用」无关的连接错误
        if (!provider.SupportsStoredProcedures)
        {
            return ResponseFormatter.Format(
                new QueryResult(new List<Dictionary<string, object?>>(), Truncated: false, ColumnCount: 0, ColumnNames: Array.Empty<string>()),
                database,
                "list_stored_procedures",
                responseFormat,
                limit: 0,
                supported: false,
                unsupportedReason: $"{provider.DisplayName} 没有存储过程/函数概念，此查询不适用。");
        }

        var result = await databaseService.ExecuteQueryAsync(
            database, provider.BuildListStoredProceduresSql(), maxRows: 5000, offset: Math.Max(0, offset));

        return ResponseFormatter.Format(result, database, "list_stored_procedures", responseFormat, 5000, limitAdjustable: false);
    }

    [McpServerTool(Name = "execute_query", ReadOnly = true, Idempotent = true)]
    [Description("在指定数据库上执行单条只读 SELECT 查询（支持 WITH CTE）。默认返回信封 JSON（含列名、行数、是否截断与提示）。")]
    public static async Task<string> ExecuteQueryAsync(
        DatabaseService databaseService,
        AuditLogger auditLogger,
        [Description("目标数据库别名（必须是已授权库）")] string database,
        [Description("要执行的 SELECT 语句（仅单条）")] string sql,
        [Description("最大返回行数，默认 100，上限 5000")] int maxRows = 100,
        [Description("跳过结果前 N 行（分页续取用），默认 0")] int offset = 0,
        [Description("响应格式：envelope（默认，含元数据与提示）/ compact（列名只出现一次，宽表可省约一半 token）/ rows（历史裸数组）")] string responseFormat = ResponseFormatter.Envelope,
        [Description("可选参数化查询参数（JSON 数组，如 [{\"name\":\"age\",\"value\":30}]）；占位符用 @name，四类库通用，可防 SQL 注入")] List<SqlParameterInput>? parameters = null,
        CancellationToken ct = default)
    {
        var limit = Math.Clamp(maxRows, 1, 5000);
        var result = await ExecuteGuardedReadAsync(
            databaseService, auditLogger, database, "execute_query", sql, limit, Math.Max(0, offset), ct, parameters);

        return ResponseFormatter.Format(result, database, "execute_query", responseFormat, limit);
    }

    [McpServerTool(Name = "test_connection", ReadOnly = true, Idempotent = true)]
    [Description("测试指定数据库的连通性，返回 Provider 类型、服务器版本、当前库名与连接耗时（毫秒）。")]
    public static async Task<string> TestConnectionAsync(
        DatabaseService databaseService,
        [Description("目标数据库别名（必须是已授权库，可通过 list_databases 查看）")] string database,
        CancellationToken ct = default)
    {
        var info = await databaseService.GetServerInfoAsync(database, ct: ct);

        var payload = new Dictionary<string, object?>
        {
            ["database"] = info.Database,
            ["provider"] = info.ProviderId,
            ["display_name"] = info.DisplayName,
            ["connected"] = true,
            ["latency_ms"] = info.LatencyMs,
            ["server_version"] = info.ServerVersion,
            ["current_database"] = info.CurrentDatabase
        };

        return JsonSerializer.Serialize(payload, DatabaseService.SerializerOptions);
    }

    [McpServerTool(Name = "export_query_result", ReadOnly = true, Idempotent = true)]
    [Description("执行只读 SELECT 查询并导出为 csv / markdown / json / xlsx。csv 保持机器可读纯净（不附加提示），json 返回含截断信息的信封，markdown 会在末尾附提示；xlsx 返回 base64 字符串（保存为 .xlsx 打开）。")]
    public static async Task<string> ExportQueryResultAsync(
        DatabaseService databaseService,
        AuditLogger auditLogger,
        [Description("目标数据库别名（必须是已授权库）")] string database,
        [Description("要执行的 SELECT 语句（仅单条）")] string sql,
        [Description("导出格式：csv（默认）/ markdown / json / xlsx")] string format = "csv",
        [Description("最大导出行数，默认 100，上限 5000")] int maxRows = 100,
        [Description("跳过结果前 N 行（分页续取用），默认 0")] int offset = 0,
        [Description("可选参数化查询参数（JSON 数组，如 [{\"name\":\"age\",\"value\":30}]）；占位符用 @name，四类库通用")] IEnumerable<SqlParameterInput>? parameters = null,
        CancellationToken ct = default)
    {
        var limit = Math.Clamp(maxRows, 1, 5000);
        var result = await ExecuteGuardedReadAsync(
            databaseService, auditLogger, database, "export_query_result", sql, limit, Math.Max(0, offset), ct, parameters);

        var normalizedFormat = format.Trim().ToLowerInvariant();
        return normalizedFormat switch
        {
            "csv" => ExportFormatter.ToCsv(result),
            "markdown" or "md" => ExportFormatter.ToMarkdown(result) + MarkdownNotice(result, limit),
            "json" => ResponseFormatter.Format(result, database, "export_query_result", ResponseFormatter.Envelope, limit),
            "xlsx" => ExportFormatter.ToXlsx(result),
            _ => throw new McpException($"不支持的导出格式「{format}」。支持的格式：csv、markdown、json、xlsx。")
        };
    }

    /// <summary>Markdown 是给人/模型阅读的散文形态，提示直接附在末尾即可。</summary>
    private static string MarkdownNotice(QueryResult result, int limit) =>
        result.Truncated
            ? $"\n\n（注意：结果超过 {limit} 行已截断。可用 offset 续取，或缩小查询范围。）"
            : string.Empty;

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
        int offset,
        CancellationToken ct,
        IEnumerable<SqlParameterInput>? parameters = null)
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

        return await databaseService.ExecuteReadOnlyQueryAsync(database, sql, limit, parameters, ct: ct, offset: offset);
    }

    [McpServerTool(Name = "health_check", ReadOnly = true, Idempotent = true)]
    [Description("对所有已授权数据库做连通性探测（并行、单库最多 5 秒），返回每库的连通状态、服务器版本与耗时（毫秒）。用于快速判断哪些库当前可达。")]
    public static async Task<string> HealthCheckAsync(
        DatabaseService databaseService,
        CancellationToken ct = default)
    {
        var results = await databaseService.GetAllServerInfoAsync(5, ct);
        var payload = results.Select(result => new Dictionary<string, object?>
        {
            ["database"] = result.Database,
            ["connected"] = result.Info is not null,
            ["server_version"] = result.Info?.ServerVersion,
            ["latency_ms"] = result.Info?.LatencyMs,
            ["error"] = result.Error
        }).ToList();

        return JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["count"] = payload.Count, ["databases"] = payload },
            DatabaseService.SerializerOptions);
    }

    [McpServerTool(Name = "table_exists", ReadOnly = true, Idempotent = true)]
    [Description("判断指定表是否存在（按 schema.表名 或 表名 解析）。返回 database / schema / table / exists；exists=true/false 为确定结论，exists=null 表示无法判定（原因见 error）。")]
    public static async Task<string> TableExistsAsync(
        DatabaseService databaseService,
        [Description("目标数据库别名（必须是已授权库）")] string database,
        [Description("表名，格式为 \"表名\" 或 \"schema.表名\"，例如 dbo.Users")] string table)
    {
        // 未授权库别名 / 非法表名属于调用方错误，直接抛出（不吞成 exists 结论）
        var provider = databaseService.GetProvider(database);
        var (schema, tableName) = provider.SplitTableName(table);

        // 复用 Provider 的列探查 SQL：返回 0 行 = 确定不存在；探查抛错（连接失败/超时等）= 无法判定，
        // 以 exists=null + error 表达，避免把"连不上"误导成"表不存在"而触发错误的建表决策（H2）。
        var (sql, parameters) = provider.BuildDescribeColumnsQuery(schema, tableName);
        bool? exists = null;
        string? error = null;
        try
        {
            var result = await databaseService.ExecuteQueryAsync(database, sql, maxRows: 1, parameters);
            exists = result.Rows.Count > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpException ex)
        {
            // 错误文案已在服务层脱敏；此处仅标记"无法判定"
            exists = null;
            error = ex.Message;
        }

        return JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["database"] = database,
                ["schema"] = schema,
                ["table"] = tableName,
                ["exists"] = exists,
                ["error"] = error
            },
            DatabaseService.SerializerOptions);
    }
}
