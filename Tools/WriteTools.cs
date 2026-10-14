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
/// TRUNCATE 附整表清空警示；黑名单（全局 + 各库附加）命中即拒绝；所有守卫拒绝与写操作均记审计日志。
/// 方言差异：MySQL 的 DDL 隐式提交不可演练，confirm=false 时不执行仅提示。
/// </summary>
[McpServerToolType]
public class WriteTools
{
    [McpServerTool(Name = "execute_non_query", Destructive = true, Idempotent = false)]
    [Description(
        "在指定数据库上执行 INSERT/UPDATE/DELETE 写操作（仅单条语句，四类数据库均支持）。" +
        "默认 confirm=false 为演练模式：语句在事务中执行后回滚，返回预估影响行数，不产生实际变更；" +
        "确认无误后以 confirm=true 重新调用同一语句才会真正提交。")]
    public static async Task<string> ExecuteNonQueryAsync(
        DatabaseService databaseService,
        SqlGuardService sqlGuardService,
        AuditLogger auditLogger,
        IOptions<SqlServerOptions> options,
        [Description("目标数据库别名（必须是已授权库）")] string database,
        [Description("要执行的 INSERT/UPDATE/DELETE 语句（仅单条）")] string sql,
        [Description("是否确认提交。false=演练模式（回滚）；true=真正提交")] bool confirm = false,
        [Description("可选参数化查询参数（JSON 数组，如 [{\"name\":\"age\",\"value\":30}]）；占位符用 @name，四类库通用，可防 SQL 注入")] List<SqlParameterInput>? parameters = null,
        CancellationToken ct = default)
    {
        const string tool = "execute_non_query";
        var settings = options.Value;
        EnsureWriteAllowed(settings, auditLogger, database, tool, sql);

        var provider = databaseService.GetProvider(database);
        if (!provider.SupportsWrite)
        {
            auditLogger.LogGuardRejected(database, tool, "write_not_supported", sql);
            throw new McpException(
                $"数据库「{database}」的类型（{provider.DisplayName}）暂不支持写操作。");
        }

        // 统一守卫（单语句 / 黑名单 / CR-1 例程拦截 / 类型白名单）在服务层实现，拒绝自动入审计；
        // 服务层写入入口会复用同一实现，任何调用方都无法绕过
        var allowedTypes = new[] { SqlStatementType.Insert, SqlStatementType.Update, SqlStatementType.Delete };
        var statementType = databaseService.ValidateWriteStatement(database, sql, allowedTypes, tool);

        // 无 WHERE 的 UPDATE/DELETE 不硬拦截：允许演练（返回全表预估影响行数），提交时需显式 confirm=true
        var affectsWholeTable = statementType is SqlStatementType.Update or SqlStatementType.Delete
            && sqlGuardService.IsUpdateOrDeleteWithoutWhere(sql);

        var affected = await databaseService.ExecuteInTransactionAsync(
            database,
            sql,
            commit: confirm,
            allowedTypes: allowedTypes,
            parameters: parameters,
            ct: ct);
        auditLogger.LogWrite(database, provider.ProviderId, "execute_non_query", confirm, affected, sql);

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
        "在指定数据库上执行 DDL 结构变更（CREATE/ALTER/DROP/TRUNCATE 等，仅单条语句，四类数据库均支持）。" +
        "默认 confirm=false 为演练模式（事务中执行后回滚）；confirm=true 才真正提交。" +
        "注意：CREATE/ALTER/DROP DATABASE 等实例级操作会被黑名单直接拒绝；" +
        "MySQL 的 DDL 会隐式提交、无法演练，confirm=false 时仅返回提示不执行。")]
    public static async Task<string> ExecuteDdlAsync(
        DatabaseService databaseService,
        SqlGuardService sqlGuardService,
        AuditLogger auditLogger,
        IOptions<SqlServerOptions> options,
        [Description("目标数据库别名（必须是已授权库）")] string database,
        [Description("要执行的 DDL 语句（仅单条）")] string sql,
        [Description("是否确认提交。false=演练模式（回滚）；true=真正提交")] bool confirm = false,
        [Description("可选参数化查询参数（JSON 数组，如 [{\"name\":\"age\",\"value\":30}]）；占位符用 @name，四类库通用")] List<SqlParameterInput>? parameters = null,
        CancellationToken ct = default)
    {
        const string tool = "execute_ddl";
        var settings = options.Value;
        EnsureWriteAllowed(settings, auditLogger, database, tool, sql);

        var provider = databaseService.GetProvider(database);
        if (!provider.SupportsWrite)
        {
            auditLogger.LogGuardRejected(database, tool, "write_not_supported", sql);
            throw new McpException(
                $"数据库「{database}」的类型（{provider.DisplayName}）暂不支持写操作。");
        }

        // 统一守卫先行（含 CR-1 例程拦截）：即便后续因方言跳过执行，非法语句也应被拒绝并留痕
        var allowedTypes = new[] { SqlStatementType.Ddl };
        databaseService.ValidateWriteStatement(database, sql, allowedTypes, tool);

        // DDL 不可事务化的方言（MySQL InnoDB 隐式提交）：演练模式不执行，避免"以为回滚了实际已提交"
        if (!provider.SupportsTransactionalDdl && !confirm)
        {
            auditLogger.LogGuardRejected(database, tool, "ddl_not_rehearsable_skipped", sql);
            return $"当前数据库类型（{provider.DisplayName}）的 DDL 会立即提交、无法进入事务演练模式（执行后不能回滚）。" +
                   "已跳过执行。请人工确认语句无误后，以 confirm=true 重新调用同一语句以实际执行。";
        }

        var affected = await databaseService.ExecuteInTransactionAsync(
            database,
            sql,
            commit: confirm,
            allowedTypes: allowedTypes,
            parameters: parameters,
            ct: ct);
        auditLogger.LogWrite(database, provider.ProviderId, tool, confirm, affected, sql);

        var truncateWarning = sqlGuardService.IsTruncateStatement(sql)
            ? "注意：TRUNCATE 会直接清空整表，不受 WHERE 限制。"
            : string.Empty;

        return confirm
            ? $"DDL 已提交执行（影响行数：{affected}，DDL 语句该值通常为 -1）。{truncateWarning}"
            : $"演练模式：DDL 已在事务中执行并回滚，未产生实际变更（影响行数预估：{affected}）。" +
              $"{truncateWarning}如确认无误，请以 confirm=true 重新调用同一语句。";
    }

    private static void EnsureWriteAllowed(
        SqlServerOptions settings, AuditLogger auditLogger, string database, string tool, string sql)
    {
        if (!settings.AllowWrite)
        {
            // 写开关关闭时的拒绝同样入审计（I4）
            auditLogger.LogGuardRejected(database, tool, "write_disabled", sql);
            throw new McpException(
                "写操作已被禁用（appsettings.json 中 SqlServer:AllowWrite=false）。" +
                "如需启用写操作，请将该配置项改为 true 并重启 MCP Server。");
        }
    }
}
