using Microsoft.Extensions.Logging.Abstractions;
using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// 看板探测缓存测试：TTL 命中与过期、并发首请求合并、结果原样透传、
/// 探测失败不写缓存、时钟回拨容错。
/// 探测委托为测试替身，**不建立任何真实数据库连接**。
/// </summary>
public sealed class DashboardProbeCacheTests
{
    private static DatabaseProbeResult Ok(string database) =>
        new(database, new ServerInfo(database, "sqlserver", "SQL Server", "16.0.1000", database, 12), null);

    private static DatabaseProbeResult Fail(string database, string error) =>
        new(database, null, error);

    private static DashboardProbeCache Create(
        DatabaseProbeHandler probe,
        TimeSpan? ttl = null,
        Func<DateTimeOffset>? clock = null) =>
        new(probe, NullLogger<DashboardProbeCache>.Instance, ttl, clock);

    [Fact]
    public async Task TTL内重复读取只探测一次且第二次标记为缓存命中()
    {
        var calls = 0;
        var now = new DateTimeOffset(2026, 10, 15, 3, 0, 0, TimeSpan.Zero);
        DatabaseProbeHandler probe = (_, _) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<DatabaseProbeResult>>(new[] { Ok("DbA") });
        };

        var cache = Create(probe, TimeSpan.FromSeconds(10), () => now);

        var first = await cache.GetAsync();
        now = now.AddSeconds(5);
        var second = await cache.GetAsync();

        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(1, calls);
        Assert.Equal(first.ProbedAt, second.ProbedAt);
    }

    [Fact]
    public async Task TTL过期后重新探测且探测时间被刷新()
    {
        var calls = 0;
        var now = new DateTimeOffset(2026, 10, 15, 3, 0, 0, TimeSpan.Zero);
        DatabaseProbeHandler probe = (_, _) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<DatabaseProbeResult>>(new[] { Ok("DbA") });
        };

        var cache = Create(probe, TimeSpan.FromSeconds(10), () => now);

        var first = await cache.GetAsync();
        now = now.AddSeconds(11);
        var second = await cache.GetAsync();

        Assert.Equal(2, calls);
        Assert.False(second.FromCache);
        Assert.True(second.ProbedAt > first.ProbedAt);
    }

    [Fact]
    public async Task TTL边界上刚好等于存活时间时仍命中缓存()
    {
        var calls = 0;
        var now = new DateTimeOffset(2026, 10, 15, 3, 0, 0, TimeSpan.Zero);
        DatabaseProbeHandler probe = (_, _) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<DatabaseProbeResult>>(new[] { Ok("DbA") });
        };

        var cache = Create(probe, TimeSpan.FromSeconds(10), () => now);

        await cache.GetAsync();
        now = now.AddSeconds(10);
        var second = await cache.GetAsync();

        Assert.True(second.FromCache);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task 并发首请求只触发一次探测()
    {
        var calls = 0;
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        DatabaseProbeHandler probe = async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            return new[] { Ok("DbA") };
        };

        var cache = Create(probe, TimeSpan.FromSeconds(10));

        var pending = Enumerable.Range(0, 8).Select(_ => cache.GetAsync()).ToArray();

        // 等首个请求真正进入探测阶段，再放行——确保其余 7 个请求确实处于等待状态
        await started.Task;
        release.SetResult();
        var snapshots = await Task.WhenAll(pending);

        Assert.Equal(1, calls);
        // 并发请求中必定只有一个执行了真实探测，其余均为缓存命中
        Assert.Single(snapshots, snapshot => !snapshot.FromCache);
    }

    [Fact]
    public async Task 探测结果原样返回包含单库失败条目()
    {
        DatabaseProbeHandler probe = (_, _) => Task.FromResult<IReadOnlyList<DatabaseProbeResult>>(
            new[] { Ok("DbA"), Fail("DbB", "连接超时，请检查主机可达性。") });

        var cache = Create(probe);

        var snapshot = await cache.GetAsync();

        Assert.Equal(2, snapshot.Results.Count);
        Assert.NotNull(snapshot.Results[0].Info);
        Assert.Null(snapshot.Results[1].Info);
        Assert.Equal("连接超时，请检查主机可达性。", snapshot.Results[1].Error);
    }

    [Fact]
    public async Task 探测抛异常时不写入缓存且下次重新探测()
    {
        var calls = 0;
        DatabaseProbeHandler probe = (_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                throw new InvalidOperationException("探测失败");
            }

            return Task.FromResult<IReadOnlyList<DatabaseProbeResult>>(new[] { Ok("DbA") });
        };

        var cache = Create(probe);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync());

        var second = await cache.GetAsync();

        Assert.Equal(2, calls);
        Assert.False(second.FromCache);
        Assert.Single(second.Results);
    }

    [Fact]
    public async Task 探测超时参数透传给探测委托()
    {
        var captured = -1;
        DatabaseProbeHandler probe = (timeoutSeconds, _) =>
        {
            captured = timeoutSeconds;
            return Task.FromResult<IReadOnlyList<DatabaseProbeResult>>(Array.Empty<DatabaseProbeResult>());
        };

        var cache = Create(probe);

        await cache.GetAsync(3);

        Assert.Equal(3, captured);
    }

    [Fact]
    public async Task 时钟回拨时不判定为过期()
    {
        var calls = 0;
        var now = new DateTimeOffset(2026, 10, 15, 3, 0, 0, TimeSpan.Zero);
        DatabaseProbeHandler probe = (_, _) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<DatabaseProbeResult>>(new[] { Ok("DbA") });
        };

        var cache = Create(probe, TimeSpan.FromSeconds(10), () => now);

        await cache.GetAsync();
        // 模拟 NTP 校正导致的时钟回拨：不应触发重新探测
        now = now.AddSeconds(-30);
        var second = await cache.GetAsync();

        Assert.True(second.FromCache);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task 取消令牌在探测前已取消时不发起探测()
    {
        var calls = 0;
        DatabaseProbeHandler probe = (_, _) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<DatabaseProbeResult>>(Array.Empty<DatabaseProbeResult>());
        };

        var cache = Create(probe);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync(5, source.Token));

        Assert.Equal(0, calls);
    }
}
