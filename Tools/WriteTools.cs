using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SqlServerMcp.Configuration;
using SqlServerMcp.Services;

namespace SqlServerMcp.Tools;

/// <summary>
/// 写操作 MCP 工具集：INSERT/UPDATE/DELETE 与 DDL。
/// 安全机制：confirm=false 时进入事务演练模式（执行后回滚，返回预估影响行数）；
/// confirm=true 才真正提交。无 WHERE 的 UPDATE/DELETE 会在演练与提交时附全表影响警告；
/// TRUNCATE 附整表清空警示；黑名单关键词直接拒绝。
/// </summary>
[McpServerToolType]
public class WriteTools
{
    [McpServerTool(Name = "execute_non_query", Destructive = true, Idempotent = false)]
    [Description(
        "在指定数据库上执行 INSERT/UPDATE/DELETE 写操作（仅单条语句）。" +
        "默认 confirm=false 为演练模式：语句在事务中执行后回滚，返回预估影响行数，不产生实际变更；" +
        "确认无误后以 confirm=true 重新调用同一语句才会真正提交。")]
    public static async Task<string> ExecuteNonQueryAsync(
        DatabaseService databaseService,
        SqlGuardService sqlGuardService,
        IOptions<SqlServerOptions> options,
        [Description("目标数据库别名（必须是已授权库）")] string database,
        [Description("要执行的 INSERT/UPDATE/DELETE 语句（仅单条）")] string sql,
        [Description("是否确认提交。false=演练模式（回滚）；true=真正提交")] bool confirm = false,
        CancellationToken ct = default)
    {
        var settings = options.Value;
        EnsureWriteAllowed(settings);

        var provider = databaseService.GetProvider(database);
        if (!provider.SupportsWrite)
        {
            throw new McpException(
                $"数据库「{database}」的类型（{provider.DisplayName}）暂不支持写操作，当前仅 SQL Server 已开放，" +
                "写能力将在后续迭代中支持。");
        }

        sqlGuardService.AssertSingleStatement(sql, "execute_non_query");
        sqlGuardService.AssertNoDangerousKeywords(sql, settings.DangerousKeywords);

        var statementType = sqlGuardService.ClassifyStatement(sql);
        if (statementType is not (SqlStatementType.Insert or SqlStatementType.Update or SqlStatementType.Delete))
        {
            throw new McpException(
                "execute_non_query 仅支持 INSERT/UPDATE/DELETE 语句。" +
                "查询请使用 execute_query，结构变更（CREATE/ALTER/DROP 等）请使用 execute_ddl。");
        }

        // 无 WHERE 的 UPDATE/DELETE 不再硬拦截：允许演练（返回全表预估影响行数），提交时需显式 confirm=true
        var affectsWholeTable = statementType is SqlStatementType.Update or SqlStatementType.Delete
            && sqlGuardService.IsUpdateOrDeleteWithoutWhere(sql);

        var affected = await databaseService.ExecuteInTransactionAsync(database, sql, commit: confirm, ct);

        if (confirm)
        {
            var committedWarning = affectsWholeTable ? "（注意：该语句无 WHERE 子句，已影响全表所有行）" : string.Empty;
            return $"已提交执行，影响行数：{affected}。{committedWarning}";
        }

        var dryRunWarning = affectsWholeTable
            ? "注意：该语句无 WHERE 子句，将影响全表所有行。"
            : string.Empty;
        return $"演练模式：语句已在事务中执行并回滚，未产生实际变更。预估影响行数：{affected}。" +
              $"{dryRunWarning}如确认无误，请以 confirm=true 重新调用同一语句。";
    }

    [McpServerTool(Name = "execute_ddl", Destructive = true, Idempotent = false)]
    [Description(
        "在指定数据库上执行 DDL 结构变更（CREATE/ALTER/DROP/TRUNCATE 等，仅单条语句）。" +
        "默认 confirm=false 为演练模式（事务中执行后回滚）；confirm=true 才真正提交。" +
        "注意：CREATE/ALTER/DROP DATABASE 等实例级操作会被黑名单直接拒绝。")]
    public static async Task<string> ExecuteDdlAsync(
        DatabaseService databaseService,
        SqlGuardService sqlGuardService,
        IOptions<SqlServerOptions> options,
        [Description("目标数据库别名（必须是已授权库）")] string database,
        [Description("要执行的 DDL 语句（仅单条）")] string sql,
        [Description("是否确认提交。false=演练模式（回滚）；true=真正提交")] bool confirm = false,
        CancellationToken ct = default)
    {
        var settings = options.Value;
        EnsureWriteAllowed(settings);

        var provider = databaseService.GetProvider(database);
        if (!provider.SupportsWrite)
        {
            throw new McpException(
                $"数据库「{database}」的类型（{provider.DisplayName}）暂不支持写操作，当前仅 SQL Server 已开放，" +
                "写能力将在后续迭代中支持。");
        }

        sqlGuardService.AssertSingleStatement(sql, "execute_ddl");
        sqlGuardService.AssertNoDangerousKeywords(sql, settings.DangerousKeywords);

        var statementType = sqlGuardService.ClassifyStatement(sql);
        if (statementType != SqlStatementType.Ddl)
        {
            throw new McpException(
                "execute_ddl 仅支持 DDL 结构变更语句（CREATE/ALTER/DROP/TRUNCATE 等）。" +
                "数据增删改请使用 execute_non_query，查询请使用 execute_query。");
        }

        var affected = await databaseService.ExecuteInTransactionAsync(database, sql, commit: confirm, ct);

        var truncateWarning = sqlGuardService.IsTruncateStatement(sql)
            ? "注意：TRUNCATE 会直接清空整表，不受 WHERE 限制，且数据量较大时不可按行回滚语义请知悉。"
            : string.Empty;

        return confirm
            ? $"DDL 已提交执行（影响行数：{affected}，DDL 语句该值通常为 -1）。{truncateWarning}"
            : $"演练模式：DDL 已在事务中执行并回滚，未产生实际变更（影响行数预估：{affected}）。" +
              $"{truncateWarning}如确认无误，请以 confirm=true 重新调用同一语句。";
    }

    private static void EnsureWriteAllowed(SqlServerOptions settings)
    {
        if (!settings.AllowWrite)
        {
            throw new McpException(
                "写操作已被禁用（appsettings.json 中 SqlServer:AllowWrite=false）。" +
                "如需启用写操作，请将该配置项改为 true 并重启 MCP Server。");
        }
    }
}
