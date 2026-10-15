using System.Globalization;

namespace SqlServerMcp.Services;

/// <summary>看板审计统计的逐日输入：某 UTC 日期的审计条目、文件是否存在与损坏行数。</summary>
/// <param name="Date">UTC 日期（与 audit-YYYYMMDD.log 的文件名口径一致）。</param>
/// <param name="Entries">该日命中的审计条目（可能为空）。</param>
/// <param name="FileFound">该日是否**存在**日志文件——用于区分「当天没有记录」与「文件不存在」。</param>
/// <param name="SkippedLines">该日跳过的损坏行数（非 JSON 或非对象行）。</param>
public sealed record DashboardAuditDay(
    DateOnly Date,
    IReadOnlyList<AuditEntry> Entries,
    bool FileFound,
    int SkippedLines = 0);

/// <summary>看板统计中的一行分布（按数据库或按工具计数）。</summary>
public sealed record DashboardAuditBucket(string Name, int Count);

/// <summary>看板逐日趋势中的一天（日期连续，缺失日期补 0，便于前端直接绘制柱状图）。</summary>
public sealed record DashboardAuditDayTrend(DateOnly Date, int Write, int GuardRejected, int WriteFailed);

/// <summary>
/// 看板审计统计结果。字段刻意扁平且全部为确定值（无 null），
/// 让前端不必到处写兜底判断。
/// </summary>
/// <param name="TotalEntries">统计到的条目总数。</param>
/// <param name="ByEvent">按事件类型计数（三个已知事件恒存在，未知事件原样追加）。</param>
/// <param name="ByMode">按写操作模式计数（commit / dry-run 恒存在）。</param>
/// <param name="AffectedRowsCommitted">**已提交**写操作的影响行数合计（演练回滚不计入真实变更）。</param>
/// <param name="ByDatabase">按数据库别名分布（计数降序、同名升序）。</param>
/// <param name="ByTool">按工具名分布（计数降序、同名升序）。</param>
/// <param name="ByDay">逐日趋势（日期升序且连续）。</param>
/// <param name="SkippedLines">损坏行合计。</param>
/// <param name="MissingFileDays">日志文件缺失的天数。</param>
public sealed record DashboardAuditSummaryResult(
    int TotalEntries,
    IReadOnlyDictionary<string, int> ByEvent,
    IReadOnlyDictionary<string, int> ByMode,
    long AffectedRowsCommitted,
    IReadOnlyList<DashboardAuditBucket> ByDatabase,
    IReadOnlyList<DashboardAuditBucket> ByTool,
    IReadOnlyList<DashboardAuditDayTrend> ByDay,
    int SkippedLines,
    int MissingFileDays);

/// <summary>
/// 看板审计统计（纯函数层，**无任何 I/O**，可直接单测）。
/// 把逐日读取的审计条目聚合为事件计数、写操作模式分布、影响行数合计、
/// 按库/按工具分布与逐日趋势。
/// <para>
/// 设计约束：
/// <list type="bullet">
/// <item>**不抛异常**：字段缺失、事件名未知、条目为 null 都必须得到确定的结构化结果——
/// 看板是只读展示页，任何解析意外都不应让整页空白；</item>
/// <item>逐日趋势按日期升序**连续**输出，缺失日期补 0，前端可直接按数组下标绘制；</item>
/// <item>影响行数只累计 <c>mode=commit</c> 的写事件：演练（dry-run）已回滚，不代表真实变更。</item>
/// </list>
/// </para>
/// </summary>
public static class DashboardAuditSummary
{
    /// <summary>写操作事件（与 <see cref="AuditLogger"/> 落盘的事件名一致）。</summary>
    public const string EventWrite = "write";

    /// <summary>守卫拒绝事件。</summary>
    public const string EventGuardRejected = "guard_rejected";

    /// <summary>写执行失败事件。</summary>
    public const string EventWriteFailed = "write_failed";

    /// <summary>已提交。</summary>
    public const string ModeCommit = "commit";

    /// <summary>演练模式（已在事务中回滚）。</summary>
    public const string ModeDryRun = "dry-run";

    /// <summary>取值缺失时的归并名（原样透出给前端，避免出现空字符串键）。</summary>
    public const string Unknown = "(未知)";

    private static readonly string[] KnownEvents = { EventWrite, EventGuardRejected, EventWriteFailed };
    private static readonly string[] KnownModes = { ModeCommit, ModeDryRun };

    /// <summary>
    /// 聚合逐日审计数据。<paramref name="days"/> 为 null 或空集合时返回全零结果（而不是抛异常）。
    /// 日期重复时按同一日期合并计数，趋势行只输出一次。
    /// </summary>
    public static DashboardAuditSummaryResult Summarize(IEnumerable<DashboardAuditDay>? days)
    {
        var ordered = (days ?? Enumerable.Empty<DashboardAuditDay>())
            .Where(day => day is not null)
            .OrderBy(day => day.Date)
            .ToList();

        var byEvent = NewCounter(StringComparer.Ordinal);
        var byMode = NewCounter(StringComparer.Ordinal);
        var byDatabase = NewCounter(StringComparer.OrdinalIgnoreCase);
        var byTool = NewCounter(StringComparer.OrdinalIgnoreCase);

        // 按日期合并（同一天传入多次时不重复输出趋势行）
        var trendByDate = new SortedDictionary<DateOnly, (int Write, int Rejected, int Failed)>();
        var total = 0;
        long affectedRows = 0;
        var skipped = 0;
        var missing = 0;
        var countedFileDays = new HashSet<DateOnly>();

        foreach (var day in ordered)
        {
            if (!countedFileDays.Add(day.Date))
            {
                // 同一天重复传入：不再重复计「文件缺失」，但条目仍参与计数
                if (!day.FileFound)
                {
                    missing++;
                }
            }
            else if (!day.FileFound)
            {
                missing++;
            }

            skipped += Math.Max(0, day.SkippedLines);

            var write = 0;
            var rejected = 0;
            var failed = 0;

            foreach (var entry in day.Entries ?? Array.Empty<AuditEntry>())
            {
                if (entry is null)
                {
                    continue;
                }

                total++;

                var evt = NormalizeKey(entry.Event);
                Increment(byEvent, evt);

                switch (evt)
                {
                    case EventWrite:
                        write++;
                        break;
                    case EventGuardRejected:
                        rejected++;
                        break;
                    case EventWriteFailed:
                        failed++;
                        break;
                }

                Increment(byDatabase, NormalizeLabel(entry.Database));
                Increment(byTool, NormalizeLabel(entry.Tool));

                if (evt != EventWrite)
                {
                    continue;
                }

                var mode = NormalizeKey(entry.Mode);
                Increment(byMode, mode);

                // 仅已提交的写操作计入真实影响行数；负数（异常数据）忽略
                if (mode == ModeCommit && entry.AffectedRows is > 0)
                {
                    affectedRows += entry.AffectedRows.Value;
                }
            }

            if (trendByDate.TryGetValue(day.Date, out var existing))
            {
                trendByDate[day.Date] = (existing.Write + write, existing.Rejected + rejected, existing.Failed + failed);
            }
            else
            {
                trendByDate[day.Date] = (write, rejected, failed);
            }
        }

        // 三个已知事件 / 两个已知模式恒存在（值为 0），保证前端契约稳定
        foreach (var name in KnownEvents)
        {
            byEvent.TryAdd(name, 0);
        }

        foreach (var name in KnownModes)
        {
            byMode.TryAdd(name, 0);
        }

        return new DashboardAuditSummaryResult(
            total,
            byEvent,
            byMode,
            affectedRows,
            ToBuckets(byDatabase),
            ToBuckets(byTool),
            trendByDate.Select(pair => new DashboardAuditDayTrend(
                pair.Key,
                pair.Value.Write,
                pair.Value.Rejected,
                pair.Value.Failed)).ToList(),
            skipped,
            missing);
    }

    /// <summary>
    /// 生成连续日期窗口（含首尾），升序。用于把稀疏的逐日数据补齐成可直接绘图的序列。
    /// <paramref name="end"/> 早于 <paramref name="start"/> 时返回空序列；跨度上限 366 天，防止异常入参造成的超长序列。
    /// </summary>
    public static IReadOnlyList<DateOnly> BuildDateWindow(DateOnly start, DateOnly end)
    {
        var days = end.DayNumber - start.DayNumber;
        if (days < 0)
        {
            return Array.Empty<DateOnly>();
        }

        var count = Math.Min(days + 1, 366);
        var result = new List<DateOnly>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(start.AddDays(i));
        }

        return result;
    }

    /// <summary>把「日期 → 条目/文件存在性」的查找结果对齐到连续日期窗口，缺失日期补空（供摘要正确补零）。</summary>
    public static IReadOnlyList<DashboardAuditDay> AlignToWindow(
        IReadOnlyList<DateOnly> window,
        Func<DateOnly, AuditQueryResult> lookup)
    {
        var result = new List<DashboardAuditDay>(window.Count);
        foreach (var date in window)
        {
            AuditQueryResult queryResult;
            try
            {
                queryResult = lookup(date);
            }
            catch (Exception)
            {
                // 单日读取失败不得中断整个窗口：按「文件缺失」处理，前端会体现为缺口
                result.Add(new DashboardAuditDay(date, Array.Empty<AuditEntry>(), FileFound: false));
                continue;
            }

            result.Add(new DashboardAuditDay(
                date,
                queryResult.Entries ?? Array.Empty<AuditEntry>(),
                queryResult.FileFound,
                queryResult.SkippedLines));
        }

        return result;
    }

    private static Dictionary<string, int> NewCounter(StringComparer comparer) => new(comparer);

    private static void Increment(Dictionary<string, int> counter, string key) =>
        counter[key] = counter.TryGetValue(key, out var current) ? current + 1 : 1;

    /// <summary>事件名/模式名归一化：去空白 + 转小写；空值归为 <see cref="Unknown"/>。</summary>
    private static string NormalizeKey(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? Unknown : text.ToLowerInvariant();
    }

    /// <summary>数据库别名/工具名归一化：仅去空白（保留大小写便于阅读），空值归为 <see cref="Unknown"/>。</summary>
    private static string NormalizeLabel(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? Unknown : text;
    }

    /// <summary>计数降序、名称升序——保证同一份输入的输出顺序稳定（便于快照断言）。</summary>
    private static IReadOnlyList<DashboardAuditBucket> ToBuckets(Dictionary<string, int> counter) =>
        counter
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new DashboardAuditBucket(pair.Key, pair.Value))
            .ToList();

    /// <summary>格式化日期为审计文件名口径（yyyy-MM-dd），供 API 输出与前端展示。</summary>
    public static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
