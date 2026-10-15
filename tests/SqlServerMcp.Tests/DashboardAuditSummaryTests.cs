using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// 看板审计统计纯函数测试：事件/模式计数、影响行数口径、逐日趋势补零、
/// 未知取值与脏数据容错、排序稳定性，以及日期窗口与对齐函数的边界。
/// 不触及任何 I/O（审计读取由 AuditLoggerQueryTests 覆盖）。
/// </summary>
public sealed class DashboardAuditSummaryTests
{
    private static AuditEntry Entry(
        string eventType,
        string? database = null,
        string? tool = null,
        string? mode = null,
        long? affectedRows = null) => new()
        {
            Ts = "2026-10-15T01:02:03.0000000+00:00",
            Event = eventType,
            Database = database,
            Tool = tool,
            Mode = mode,
            AffectedRows = affectedRows
        };

    private static DashboardAuditDay Day(int day, params AuditEntry[] entries) =>
        new(new DateOnly(2026, 10, day), entries, FileFound: true);

    [Fact]
    public void 空输入与null输入都返回全零结果而不抛异常()
    {
        foreach (var input in new[] { null, Array.Empty<DashboardAuditDay>() })
        {
            var result = DashboardAuditSummary.Summarize(input);

            Assert.Equal(0, result.TotalEntries);
            Assert.Equal(0, result.AffectedRowsCommitted);
            Assert.Equal(0, result.SkippedLines);
            Assert.Equal(0, result.MissingFileDays);
            Assert.Empty(result.ByDatabase);
            Assert.Empty(result.ByTool);
            Assert.Empty(result.ByDay);

            // 三个已知事件与两个已知模式恒存在（值为 0），保证前端契约稳定
            Assert.Equal(0, result.ByEvent[DashboardAuditSummary.EventWrite]);
            Assert.Equal(0, result.ByEvent[DashboardAuditSummary.EventGuardRejected]);
            Assert.Equal(0, result.ByEvent[DashboardAuditSummary.EventWriteFailed]);
            Assert.Equal(0, result.ByMode[DashboardAuditSummary.ModeCommit]);
            Assert.Equal(0, result.ByMode[DashboardAuditSummary.ModeDryRun]);
        }
    }

    [Fact]
    public void 混合事件按类型计数()
    {
        var result = DashboardAuditSummary.Summarize(new[]
        {
            Day(14,
                Entry(DashboardAuditSummary.EventWrite, "DbA", "execute_ddl", DashboardAuditSummary.ModeCommit, 3),
                Entry(DashboardAuditSummary.EventWrite, "DbA", "execute_non_query", DashboardAuditSummary.ModeDryRun, 99),
                Entry(DashboardAuditSummary.EventGuardRejected, "DbA", "execute_query"),
                Entry(DashboardAuditSummary.EventWriteFailed, "DbB", "execute_ddl"))
        });

        Assert.Equal(4, result.TotalEntries);
        Assert.Equal(2, result.ByEvent[DashboardAuditSummary.EventWrite]);
        Assert.Equal(1, result.ByEvent[DashboardAuditSummary.EventGuardRejected]);
        Assert.Equal(1, result.ByEvent[DashboardAuditSummary.EventWriteFailed]);
    }

    [Fact]
    public void 影响行数只累计已提交的写操作()
    {
        var result = DashboardAuditSummary.Summarize(new[]
        {
            Day(14,
                Entry(DashboardAuditSummary.EventWrite, "DbA", "execute_non_query", DashboardAuditSummary.ModeCommit, 10),
                Entry(DashboardAuditSummary.EventWrite, "DbA", "execute_non_query", DashboardAuditSummary.ModeCommit, 5),
                // 演练已回滚，不得计入真实影响行数
                Entry(DashboardAuditSummary.EventWrite, "DbA", "execute_non_query", DashboardAuditSummary.ModeDryRun, 1000),
                // 非写事件的 affected_rows 同样不计入
                Entry(DashboardAuditSummary.EventGuardRejected, "DbA", "execute_query", affectedRows: 999))
        });

        Assert.Equal(15, result.AffectedRowsCommitted);
        // 四个事件中只有前两条是 commit（第三条为演练回滚，第四条是守卫拒绝、根本没有 mode）
        Assert.Equal(2, result.ByMode[DashboardAuditSummary.ModeCommit]);
        Assert.Equal(1, result.ByMode[DashboardAuditSummary.ModeDryRun]);
    }

    [Fact]
    public void 影响行数为负或缺失时忽略而不产生负数合计()
    {
        var result = DashboardAuditSummary.Summarize(new[]
        {
            Day(14,
                Entry(DashboardAuditSummary.EventWrite, "DbA", "execute_ddl", DashboardAuditSummary.ModeCommit, -5),
                Entry(DashboardAuditSummary.EventWrite, "DbA", "execute_ddl", DashboardAuditSummary.ModeCommit))
        });

        Assert.Equal(0, result.AffectedRowsCommitted);
    }

    [Fact]
    public void 事件名大小写与空白被归一化()
    {
        var result = DashboardAuditSummary.Summarize(new[]
        {
            Day(14, Entry("  WRITE  ", "DbA", "execute_ddl", "COMMIT", 1))
        });

        Assert.Equal(1, result.ByEvent[DashboardAuditSummary.EventWrite]);
        Assert.Equal(1, result.ByMode[DashboardAuditSummary.ModeCommit]);
        Assert.Equal(1, result.AffectedRowsCommitted);
    }

    [Fact]
    public void 未知事件名与缺失字段被容错归并而不抛异常()
    {
        var result = DashboardAuditSummary.Summarize(new[]
        {
            Day(14,
                Entry("some_future_event"),
                Entry(DashboardAuditSummary.EventWrite))
        });

        Assert.Equal(2, result.TotalEntries);
        Assert.Equal(1, result.ByEvent["some_future_event"]);
        Assert.Equal(1, result.ByEvent[DashboardAuditSummary.EventWrite]);
        // 两条条目都没有 database 字段，因此占位名下应聚合为 2
        Assert.Equal(2, result.ByDatabase.Single(bucket => bucket.Name == DashboardAuditSummary.Unknown).Count);
        Assert.Equal(1, result.ByMode[DashboardAuditSummary.Unknown]);
    }

    [Fact]
    public void 逐日趋势按日期升序且只输出传入的日期()
    {
        var result = DashboardAuditSummary.Summarize(new[]
        {
            Day(15, Entry(DashboardAuditSummary.EventWrite, "DbA", "execute_ddl", DashboardAuditSummary.ModeCommit, 1)),
            Day(13, Entry(DashboardAuditSummary.EventGuardRejected, "DbA", "execute_query"))
        });

        Assert.Equal(2, result.ByDay.Count);
        Assert.Equal(new DateOnly(2026, 10, 13), result.ByDay[0].Date);
        Assert.Equal(1, result.ByDay[0].GuardRejected);
        Assert.Equal(0, result.ByDay[0].Write);
        Assert.Equal(new DateOnly(2026, 10, 15), result.ByDay[1].Date);
        Assert.Equal(1, result.ByDay[1].Write);
    }

    [Fact]
    public void 同一天重复传入时条目合并且趋势只输出一行()
    {
        var result = DashboardAuditSummary.Summarize(new[]
        {
            Day(14, Entry(DashboardAuditSummary.EventWrite, "DbA", "execute_ddl", DashboardAuditSummary.ModeCommit, 2)),
            Day(14, Entry(DashboardAuditSummary.EventWrite, "DbA", "execute_ddl", DashboardAuditSummary.ModeCommit, 3))
        });

        Assert.Single(result.ByDay);
        Assert.Equal(2, result.ByDay[0].Write);
        Assert.Equal(5, result.AffectedRowsCommitted);
    }

    [Fact]
    public void 缺失文件的天数与损坏行被单独统计()
    {
        var result = DashboardAuditSummary.Summarize(new[]
        {
            new DashboardAuditDay(new DateOnly(2026, 10, 13), Array.Empty<AuditEntry>(), FileFound: false, SkippedLines: 2),
            new DashboardAuditDay(new DateOnly(2026, 10, 14), Array.Empty<AuditEntry>(), FileFound: false, SkippedLines: 0),
            new DashboardAuditDay(new DateOnly(2026, 10, 15), Array.Empty<AuditEntry>(), FileFound: true, SkippedLines: 3)
        });

        Assert.Equal(2, result.MissingFileDays);
        Assert.Equal(5, result.SkippedLines);
        // 文件缺失的天仍出现在趋势里（值为 0），便于前端呈现缺口
        Assert.Equal(3, result.ByDay.Count);
    }

    [Fact]
    public void 按库与按工具分布按计数降序名称升序排列()
    {
        var result = DashboardAuditSummary.Summarize(new[]
        {
            Day(14,
                Entry(DashboardAuditSummary.EventWrite, "Beta", "execute_ddl", DashboardAuditSummary.ModeCommit, 1),
                Entry(DashboardAuditSummary.EventWrite, "Alpha", "execute_ddl", DashboardAuditSummary.ModeCommit, 1),
                Entry(DashboardAuditSummary.EventWrite, "Alpha", "execute_query", DashboardAuditSummary.ModeCommit, 1),
                Entry(DashboardAuditSummary.EventWrite, "Alpha", "execute_ddl", DashboardAuditSummary.ModeCommit, 1))
        });

        Assert.Equal(new[] { "Alpha", "Beta" }, result.ByDatabase.Select(bucket => bucket.Name));
        Assert.Equal(3, result.ByDatabase[0].Count);
        Assert.Equal(new[] { "execute_ddl", "execute_query" }, result.ByTool.Select(bucket => bucket.Name));
    }

    [Fact]
    public void 数据库别名大小写不同视为同一项()
    {
        var result = DashboardAuditSummary.Summarize(new[]
        {
            Day(14,
                Entry(DashboardAuditSummary.EventWrite, "DbA", "execute_ddl", DashboardAuditSummary.ModeCommit, 1),
                Entry(DashboardAuditSummary.EventWrite, "dba", "execute_ddl", DashboardAuditSummary.ModeCommit, 1))
        });

        var bucket = Assert.Single(result.ByDatabase);
        Assert.Equal(2, bucket.Count);
    }

    [Fact]
    public void 日期窗口连续且包含首尾()
    {
        var window = DashboardAuditSummary.BuildDateWindow(new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 15));

        Assert.Equal(3, window.Count);
        Assert.Equal(new DateOnly(2026, 10, 13), window[0]);
        Assert.Equal(new DateOnly(2026, 10, 15), window[^1]);
    }

    [Fact]
    public void 日期窗口在结束早于开始时返回空序列()
    {
        Assert.Empty(DashboardAuditSummary.BuildDateWindow(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 13)));
    }

    [Fact]
    public void 日期窗口跨度被截断到上限以内()
    {
        // 上限 366 天：避免异常入参产生超长序列
        var window = DashboardAuditSummary.BuildDateWindow(new DateOnly(2020, 1, 1), new DateOnly(2026, 10, 15));

        Assert.Equal(366, window.Count);
    }

    [Fact]
    public void 对齐窗口时单日读取异常降级为文件缺失()
    {
        var window = DashboardAuditSummary.BuildDateWindow(new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 15));

        var aligned = DashboardAuditSummary.AlignToWindow(window, date => date.Day == 14
            ? throw new InvalidOperationException("读取失败")
            : new AuditQueryResult(new[] { Entry(DashboardAuditSummary.EventWrite, "DbA", "execute_ddl", DashboardAuditSummary.ModeCommit, 1) }, 0, FileFound: true));

        Assert.Equal(2, aligned.Count);
        Assert.False(aligned[0].FileFound);
        Assert.Empty(aligned[0].Entries);
        Assert.True(aligned[1].FileFound);

        var summary = DashboardAuditSummary.Summarize(aligned);
        Assert.Equal(1, summary.TotalEntries);
        Assert.Equal(1, summary.MissingFileDays);
    }

    [Fact]
    public void 对齐窗口时条目为null的查询结果按空处理()
    {
        var window = DashboardAuditSummary.BuildDateWindow(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 15));

        var aligned = DashboardAuditSummary.AlignToWindow(window, _ => new AuditQueryResult(null!, 1, FileFound: true));

        Assert.Single(aligned);
        Assert.Empty(aligned[0].Entries);

        var summary = DashboardAuditSummary.Summarize(aligned);
        Assert.Equal(0, summary.TotalEntries);
        Assert.Equal(1, summary.SkippedLines);
    }
}
