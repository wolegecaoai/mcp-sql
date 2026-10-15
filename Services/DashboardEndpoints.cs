using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using SqlServerMcp.Configuration;

namespace SqlServerMcp.Services;

/// <summary>
/// 运维看板的只读 JSON 端点（仅在 http 模式挂载）。
/// <para>
/// **安全边界**（本层刻意收窄攻击面）：
/// <list type="bullet">
/// <item>三个端点均为只读 GET，**不接受** SQL、表名、文件名或路径参数——唯一入参是数值型
/// <c>days</c> / <c>limit</c>，且一律 Clamp；</item>
/// <item>配置摘要**逐字段构造**，绝不序列化 <see cref="SqlServerOptions"/> 本身——
/// 那会把 <c>ApiKey</c> 与全部连接串泄漏到页面上；</item>
/// <item>审计日志目录由服务端固定（<see cref="AuditLogger"/> 内部持有），此处无法指定路径。</item>
/// </list>
/// </para>
/// <para>
/// 与 MCP 工具层的约定一致：所有响应经 <see cref="DatabaseService.SerializerOptions"/> 序列化，
/// 输出恒为合法 JSON（中文不转义）。
/// </para>
/// </summary>
public static class DashboardEndpoints
{
    /// <summary>看板路由前缀（静态页面与 API 共用）。</summary>
    public const string BasePath = "/dashboard";

    /// <summary>审计统计的默认/最大回溯天数（上限防止一次请求扫描过多文件）。</summary>
    private const int DefaultSummaryDays = 7;
    private const int MaxSummaryDays = 30;

    /// <summary>「最近写操作」的默认/最大条数（与 query_audit_log 工具的 1–200 口径一致）。</summary>
    private const int DefaultRecentLimit = 50;
    private const int MaxRecentLimit = 200;

    /// <summary>「最近写操作」向前回溯的最大天数：避免当天无记录时无限扫描历史文件。</summary>
    private const int RecentLookbackDays = 30;

    /// <summary>逐日统计时单日读取条数上限（超过该值的单日会被低估，响应中以 per_day_limit 明示口径）。</summary>
    private const int SummaryPerDayLimit = 5000;

    /// <summary>连通性探测的单库超时（秒），与 list_databases / health_check 的 5 秒口径一致。</summary>
    private const int ProbeTimeoutSeconds = 5;

    /// <summary>注册三个只读端点。调用方需保证已挂载静态文件中间件。</summary>
    public static void Map(WebApplication app)
    {
        app.MapGet($"{BasePath}/api/overview", HandleOverviewAsync);
        app.MapGet($"{BasePath}/api/audit/summary", HandleAuditSummary);
        app.MapGet($"{BasePath}/api/audit/recent", HandleRecent);
    }

    /// <summary>
    /// 概览：脱敏配置摘要 + 授权库清单与能力标志 + 连通性探测结果（带 TTL 缓存）。
    /// 单个库的类型非法或探测失败只影响该条目，不会让整个端点失败。
    /// </summary>
    private static async Task<IResult> HandleOverviewAsync(
        HttpContext context,
        DatabaseService databaseService,
        DashboardProbeCache probeCache,
        IOptions<SqlServerOptions> options,
        CancellationToken ct)
    {
        MarkNoStore(context);

        var value = options.Value;
        var snapshot = await probeCache.GetAsync(ProbeTimeoutSeconds, ct);

        // 按别名索引探测结果（大小写不敏感，与 DatabaseService 的别名解析口径一致）
        var probeByName = new Dictionary<string, (ServerInfo? Info, string? Error)>(StringComparer.OrdinalIgnoreCase);
        foreach (var probe in snapshot.Results)
        {
            probeByName[probe.Database] = (probe.Info, probe.Error);
        }

        var databases = new List<Dictionary<string, object?>>();
        foreach (var (name, type, description) in databaseService.GetDatabaseList())
        {
            var item = new Dictionary<string, object?>
            {
                ["database"] = name,
                ["type"] = type,
                ["description"] = description
            };

            try
            {
                // 必须按「类型」取 Provider（按别名取会在别名不等于类型时抛错）
                var provider = databaseService.GetProviderByType(type);
                item["display_name"] = provider.DisplayName;
                item["ddl_rehearsal"] = provider.SupportsTransactionalDdl;
                item["supports_stored_procedures"] = provider.SupportsStoredProcedures;
                item["supports_write"] = provider.SupportsWrite;
            }
            catch (McpException ex)
            {
                item["type_error"] = ex.Message;
            }

            if (probeByName.TryGetValue(name, out var probe))
            {
                if (probe.Info is not null)
                {
                    item["connected"] = true;
                    item["server_version"] = probe.Info.ServerVersion;
                    item["current_database"] = probe.Info.CurrentDatabase;
                    item["latency_ms"] = probe.Info.LatencyMs;
                }
                else
                {
                    item["connected"] = false;
                    item["error"] = probe.Error ?? "探测失败（未返回错误详情）。";
                }
            }
            else
            {
                item["connected"] = false;
                item["error"] = "未取得该库的探测结果。";
            }

            databases.Add(item);
        }

        var connectedCount = databases.Count(item => item.TryGetValue("connected", out var flag) && flag is true);

        var config = new Dictionary<string, object?>
        {
            ["transport"] = value.Transport,
            ["port"] = value.HttpPort,
            ["allow_write"] = value.AllowWrite,
            ["command_timeout_seconds"] = value.CommandTimeoutSeconds,
            ["max_response_chars"] = value.MaxResponseChars,
            ["audit_retention_days"] = value.AuditRetentionDays,
            ["startup_connectivity_check"] = value.StartupConnectivityCheck,
            ["database_count"] = value.Databases.Count,
            ["dangerous_keyword_count"] = CountEffectiveKeywords(databaseService, value),
            // 仅暴露「是否已配置」，绝不输出密钥值本身
            ["api_key_configured"] = !string.IsNullOrWhiteSpace(value.ApiKey)
        };

        var payload = new Dictionary<string, object?>
        {
            ["generated_at"] = UtcNow(),
            ["summary"] = new Dictionary<string, object?>
            {
                ["database_count"] = databases.Count,
                ["connected_count"] = connectedCount,
                ["failed_count"] = databases.Count - connectedCount
            },
            ["config"] = config,
            ["databases"] = databases,
            ["cache"] = new Dictionary<string, object?>
            {
                ["from_cache"] = snapshot.FromCache,
                ["probed_at"] = FormatTimestamp(snapshot.ProbedAt),
                ["ttl_seconds"] = (int)DashboardProbeCache.DefaultTtl.TotalSeconds
            }
        };

        return Json(payload);
    }

    /// <summary>
    /// 审计统计：按 UTC 逐日读取后在内存聚合（<see cref="AuditLogger.QueryEntries"/> 只支持单日查询）。
    /// <paramref name="days"/> Clamp 到 1–30，缺失日期补 0，单日读取失败按「文件缺失」处理。
    /// </summary>
    private static IResult HandleAuditSummary(HttpContext context, AuditLogger auditLogger, int? days)
    {
        MarkNoStore(context);

        var span = Math.Clamp(days ?? DefaultSummaryDays, 1, MaxSummaryDays);
        var end = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = end.AddDays(-(span - 1));

        var window = DashboardAuditSummary.BuildDateWindow(start, end);
        var aligned = DashboardAuditSummary.AlignToWindow(
            window,
            date => auditLogger.QueryEntries(date, null, null, null, SummaryPerDayLimit));
        var summary = DashboardAuditSummary.Summarize(aligned);

        var payload = new Dictionary<string, object?>
        {
            ["generated_at"] = UtcNow(),
            ["range"] = new Dictionary<string, object?>
            {
                ["start"] = DashboardAuditSummary.FormatDate(start),
                ["end"] = DashboardAuditSummary.FormatDate(end),
                ["days"] = span,
                // 审计按 UTC 切日；前端负责换算为浏览器本地时区展示
                ["timezone"] = "UTC"
            },
            ["total_entries"] = summary.TotalEntries,
            ["by_event"] = summary.ByEvent,
            ["by_mode"] = summary.ByMode,
            ["affected_rows_total"] = summary.AffectedRowsCommitted,
            ["by_database"] = summary.ByDatabase
                .Select(bucket => new Dictionary<string, object?> { ["name"] = bucket.Name, ["count"] = bucket.Count })
                .ToList(),
            ["by_tool"] = summary.ByTool
                .Select(bucket => new Dictionary<string, object?> { ["name"] = bucket.Name, ["count"] = bucket.Count })
                .ToList(),
            ["by_day"] = summary.ByDay
                .Select(day => new Dictionary<string, object?>
                {
                    ["date"] = DashboardAuditSummary.FormatDate(day.Date),
                    ["write"] = day.Write,
                    ["guard_rejected"] = day.GuardRejected,
                    ["write_failed"] = day.WriteFailed
                })
                .ToList(),
            ["skipped_lines"] = summary.SkippedLines,
            ["missing_file_days"] = summary.MissingFileDays,
            // 明示统计口径：单日超过该条数时计数会被低估
            ["per_day_limit"] = SummaryPerDayLimit,
            ["notice"] = "ts 为 UTC 时间；mode=commit 表示已提交，dry-run 表示演练回滚。"
        };

        return Json(payload);
    }

    /// <summary>
    /// 最近审计条目（最新在前）。自当天起向前逐日读取，累计够数即停止；
    /// 最多回溯 30 天，避免日志稀少时扫描过深。<paramref name="limit"/> Clamp 到 1–200。
    /// </summary>
    private static IResult HandleRecent(HttpContext context, AuditLogger auditLogger, int? limit)
    {
        MarkNoStore(context);

        var take = Math.Clamp(limit ?? DefaultRecentLimit, 1, MaxRecentLimit);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var collected = new List<AuditEntry>();
        var scannedDays = 0;

        for (var offset = 0; offset < RecentLookbackDays && collected.Count < take; offset++)
        {
            var date = today.AddDays(-offset);
            scannedDays++;

            var result = auditLogger.QueryEntries(date, null, null, null, take);
            if (result.Entries.Count > 0)
            {
                // 更早的日期整体更早，插到列表前端以维持全局时间升序
                collected.InsertRange(0, result.Entries);
            }
        }

        // 升序列表中取末尾 take 条（最新），再倒序输出（最新在前，便于阅读）
        var sliced = collected.Count > take
            ? collected.GetRange(collected.Count - take, take)
            : collected;
        sliced.Reverse();

        var payload = new Dictionary<string, object?>
        {
            ["generated_at"] = UtcNow(),
            ["count"] = sliced.Count,
            ["limit"] = take,
            ["scanned_days"] = scannedDays,
            ["timezone"] = "UTC",
            ["entries"] = sliced.Select(ToWireFormat).ToList()
        };

        return Json(payload);
    }

    /// <summary>
    /// 投影为输出结构：跳过空字段以减小体积。字段名与 <c>query_audit_log</c> 工具保持一致，
    /// 便于使用者对照两份输出与原始日志文件。
    /// </summary>
    private static Dictionary<string, object?> ToWireFormat(AuditEntry entry)
    {
        var item = new Dictionary<string, object?>
        {
            ["ts"] = entry.Ts,
            ["event"] = entry.Event
        };

        AddIfPresent(item, "database", entry.Database);
        AddIfPresent(item, "provider", entry.Provider);
        AddIfPresent(item, "tool", entry.Tool);
        AddIfPresent(item, "mode", entry.Mode);
        AddIfPresent(item, "affected_rows", entry.AffectedRows);
        AddIfPresent(item, "reason", entry.Reason);
        AddIfPresent(item, "error", entry.Error);
        AddIfPresent(item, "sql_sha256", entry.SqlSha256);
        AddIfPresent(item, "sql_preview", entry.SqlPreview);

        return item;
    }

    private static void AddIfPresent(Dictionary<string, object?> item, string key, object? value)
    {
        if (value is null)
        {
            return;
        }

        if (value is string text && string.IsNullOrEmpty(text))
        {
            return;
        }

        item[key] = value;
    }

    /// <summary>
    /// 统计实际生效的危险关键词总数（全局配置 + 各 Provider 扩展的并集去重）。
    /// 无授权库时退回全局配置条数——此时无法按类型取到 Provider 扩展。
    /// </summary>
    private static int CountEffectiveKeywords(DatabaseService databaseService, SqlServerOptions options)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var database in databaseService.GetDatabaseNames())
        {
            foreach (var keyword in databaseService.GetEffectiveDangerousKeywords(database))
            {
                keywords.Add(keyword);
            }
        }

        return keywords.Count > 0 ? keywords.Count : options.DangerousKeywords.Count;
    }

    /// <summary>统一响应出口：恒为合法 JSON（UTF-8、中文不转义），并禁用中间缓存。</summary>
    private static IResult Json(object payload) =>
        Results.Text(
            JsonSerializer.Serialize(payload, DatabaseService.SerializerOptions),
            "application/json",
            Encoding.UTF8);

    private static void MarkNoStore(HttpContext context) =>
        context.Response.Headers.CacheControl = "no-store";

    private static string UtcNow() => FormatTimestamp(DateTimeOffset.UtcNow);

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
}
