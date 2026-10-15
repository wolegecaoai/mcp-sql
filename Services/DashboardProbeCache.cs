using Microsoft.Extensions.Logging;

namespace SqlServerMcp.Services;

/// <summary>单个授权库的连通性探测结果（单库失败只体现在 <see cref="Error"/> 上）。</summary>
/// <param name="Database">数据库别名。</param>
/// <param name="Info">探测成功时的服务器信息；失败为 null。</param>
/// <param name="Error">失败原因（已脱敏的中文提示）；成功为 null。</param>
public sealed record DatabaseProbeResult(string Database, ServerInfo? Info, string? Error);

/// <summary>
/// 批量探测委托。默认实现直接对接到
/// <see cref="DatabaseService.GetAllServerInfoAsync(int, CancellationToken)"/>；
/// 抽成委托是为了让 TTL / 并发合并等缓存行为可在**不建立任何真实连接**的前提下单测。
/// </summary>
public delegate Task<IReadOnlyList<DatabaseProbeResult>> DatabaseProbeHandler(
    int timeoutSeconds, CancellationToken cancellationToken);

/// <summary>
/// 一次连通性探测的快照（含缓存元信息，供 API 输出 <c>cache</c> 字段）。
/// </summary>
/// <param name="Results">逐库探测结果（单库失败只体现在该条目的 Error 上）。</param>
/// <param name="ProbedAt">本次探测（而非读取缓存）发生的 UTC 时间。</param>
/// <param name="FromCache">true 表示本次读取命中了 TTL 内的缓存，未重新建连。</param>
public sealed record DashboardProbeSnapshot(
    IReadOnlyList<DatabaseProbeResult> Results,
    DateTimeOffset ProbedAt,
    bool FromCache);

/// <summary>
/// 看板连通性探测的 TTL 内存缓存。
/// <para>
/// **为什么需要缓存**：默认探测实现会对每个授权库真实建立连接（并行、单库最长 5 秒）。
/// 看板是轮询页面，多个浏览器标签页或连续刷新若各自触发探测，会造成频繁建连与不可预期的延迟。
/// 缓存让同一 TTL 窗口内的所有请求共享一次探测。
/// </para>
/// <para>
/// 三个行为要点：
/// <list type="bullet">
/// <item><b>TTL</b>：默认 10 秒（<see cref="DefaultTtl"/>），可由构造参数覆盖以便单测；</item>
/// <item><b>并发合并</b>：TTL 过期时的并发首请求通过 <see cref="SemaphoreSlim"/> 串行化，
/// 且在拿到锁后**二次检查**缓存，避免缓存击穿导致的重复建连；</item>
/// <item><b>时钟注入</b>：时间来源为 <c>Func&lt;DateTimeOffset&gt;</c>（默认 <c>UtcNow</c>），
/// 使 TTL 行为无需真实等待即可测试。</item>
/// </list>
/// </para>
/// </summary>
public sealed class DashboardProbeCache
{
    /// <summary>默认缓存存活时间：短到足以反映故障恢复，长到足以覆盖页面轮询周期。</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(10);

    private readonly DatabaseProbeHandler _probe;
    private readonly ILogger<DashboardProbeCache> _logger;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>最近一次成功完成的探测结果；null 表示尚未探测过。</summary>
    private IReadOnlyList<DatabaseProbeResult>? _results;

    /// <summary>最近一次探测完成的时间（按注入的时钟取值）。</summary>
    private DateTimeOffset _probedAt;

    public DashboardProbeCache(
        DatabaseProbeHandler probe,
        ILogger<DashboardProbeCache> logger,
        TimeSpan? ttl = null,
        Func<DateTimeOffset>? clock = null)
    {
        _probe = probe;
        _logger = logger;
        _ttl = ttl ?? DefaultTtl;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 读取探测结果：TTL 内直接复用缓存，否则重新探测（并发首请求合并为一次）。
    /// 缓存**只存成功完成的探测**——探测过程中抛出（如外部取消）时不写入缓存，下次请求会重新探测。
    /// </summary>
    public async Task<DashboardProbeSnapshot> GetAsync(int timeoutSeconds = 5, CancellationToken ct = default)
    {
        if (TryReadCache(out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 二次检查：并发的第二个请求在等待锁期间，首个请求可能已完成探测
            if (TryReadCache(out var refreshed))
            {
                return refreshed;
            }

            var results = await _probe(timeoutSeconds, ct).ConfigureAwait(false);
            var now = _clock();

            _results = results;
            _probedAt = now;

            _logger.LogDebug("看板连通性探测完成：{Count} 个授权库，缓存 TTL {Ttl} 秒", results.Count, _ttl.TotalSeconds);

            return new DashboardProbeSnapshot(results, now, FromCache: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>尝试命中缓存；未探测过或已过期时返回 false（<paramref name="snapshot"/> 为 null）。</summary>
    private bool TryReadCache(out DashboardProbeSnapshot snapshot)
    {
        snapshot = null!;

        var results = _results;
        if (results is null)
        {
            return false;
        }

        // 时钟回拨（NTP 校正 / 手动改表）时 now - probedAt 为负，按命中处理，避免把回拨误判为过期而反复重探
        if (_clock() - _probedAt > _ttl)
        {
            return false;
        }

        snapshot = new DashboardProbeSnapshot(results, _probedAt, FromCache: true);
        return true;
    }
}
