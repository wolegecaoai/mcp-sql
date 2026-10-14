using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using SqlServerMcp.Services;
using SqlServerMcp.Tools;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// query_audit_log 工具测试：参数校验（日期/事件类型）、条数上限、越界防护。
/// 这些是「审计查询不得变成任意文件读取入口」的回归护栏，必须留在工具层。
/// </summary>
public sealed class AuditToolsTests : IDisposable
{
    private readonly string _directory;
    private readonly AuditLogger _logger;
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.UtcNow);

    public AuditToolsTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "sqlservermcp-audittool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _logger = new AuditLogger(_directory, NullLogger<AuditLogger>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结果
        }
    }

    private string Query(string? date = null, string? database = null, string? eventType = null, string? tool = null, int limit = 50) =>
        AuditTools.QueryAuditLog(_logger, date, database, eventType, tool, limit);

    [Theory]
    [InlineData("2026/10/14")]
    [InlineData("2026-10-14T00:00")]
    [InlineData("14-10-2026")]
    [InlineData("../20261014")]
    [InlineData("../../etc/passwd")]
    [InlineData("20261014")]
    [InlineData("2026-13-01")]
    public void 非法日期一律拒绝(string date)
    {
        var ex = Assert.Throws<McpException>(() => Query(date: date));

        Assert.Contains("日期参数格式不正确", ex.Message);
    }

    [Theory]
    [InlineData("DROP")]
    [InlineData("write_fail")]
    [InlineData("guard")]
    public void 非法事件类型一律拒绝(string eventType)
    {
        var ex = Assert.Throws<McpException>(() => Query(eventType: eventType));

        Assert.Contains("无效", ex.Message);
        Assert.Contains("guard_rejected", ex.Message);   // 提示可用值
    }

    [Theory]
    [InlineData("WRITE")]
    [InlineData("Guard_Rejected")]
    public void 事件类型过滤大小写不敏感(string eventType)
    {
        // 两种事件都写入，才能用不同大小写的过滤值各命中一次
        _logger.LogWrite("Db1", "sqlite", "execute_non_query", committed: true, affectedRows: 1, sql: "INSERT INTO t VALUES (1)");
        _logger.LogGuardRejected("Db1", "execute_ddl", "检测到危险关键词", "DROP DATABASE x");

        var result = Query(eventType: eventType);

        Assert.DoesNotContain("没有符合过滤条件", result);
        Assert.Contains(eventType.ToLowerInvariant(), result);
    }

    [Fact]
    public void 缺省日期为今天UTC()
    {
        _logger.LogWrite("Db1", "sqlite", "execute_non_query", committed: true, affectedRows: 1, sql: "INSERT INTO t VALUES (1)");

        using var document = JsonDocument.Parse(Query());
        var root = document.RootElement;

        Assert.Equal(_today.ToString("yyyy-MM-dd"), root.GetProperty("date").GetString());
        Assert.Equal(1, root.GetProperty("count").GetInt32());
    }

    [Fact]
    public void 返回结构含事件字段与提示()
    {
        _logger.LogGuardRejected("Db1", "execute_ddl", "检测到危险关键词", "DROP DATABASE x");

        using var document = JsonDocument.Parse(Query(eventType: "guard_rejected"));
        var entry = document.RootElement.GetProperty("entries")[0];

        Assert.Equal("guard_rejected", entry.GetProperty("event").GetString());
        Assert.Equal("Db1", entry.GetProperty("database").GetString());
        Assert.Contains("危险关键词", entry.GetProperty("reason").GetString());
        Assert.True(entry.TryGetProperty("sql_preview", out _));
    }

    [Fact]
    public void 空字段被裁剪以降低上下文噪声()
    {
        // write 事件没有 reason/error 字段，输出中不应出现它们
        _logger.LogWrite("Db1", "sqlite", "execute_non_query", committed: true, affectedRows: 2, sql: "INSERT INTO t VALUES (1)");

        var text = Query();

        Assert.DoesNotContain("\"reason\"", text);
        Assert.DoesNotContain("\"error\"", text);
        Assert.Contains("\"affected_rows\":2", text);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-10, 1)]
    [InlineData(9999, 200)]
    public void 条数参数被Clamp到1至200(int limit, int expected)
    {
        // 写入超过上限的条目，验证下限（1）与上限（200）都生效
        for (var i = 0; i < 250; i++)
        {
            _logger.LogWrite("Db1", "sqlite", "execute_non_query", committed: true, affectedRows: i, sql: $"INSERT INTO t VALUES ({i})");
        }

        using var document = JsonDocument.Parse(Query(limit: limit));

        Assert.Equal(expected, document.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void 日期不存在时给出说明而非空JSON()
    {
        var result = Query(date: "2020-01-01");

        Assert.Contains("未找到", result);
        Assert.Contains("2020-01-01", result);
    }

    [Fact]
    public void 无匹配记录时给出说明并报告脏行数()
    {
        // 手工写一行损坏内容 + 一行合法内容
        var logsDirectory = Path.Combine(_directory, "logs");
        Directory.CreateDirectory(logsDirectory);
        File.WriteAllLines(
            Path.Combine(logsDirectory, $"audit-{_today:yyyyMMdd}.log"),
            new[] { "不是 JSON", "{\"ts\":\"x\",\"event\":\"write\",\"database\":\"Other\"}" });

        var result = Query(database: "NoSuchDb");

        Assert.Contains("没有符合过滤条件", result);
        Assert.Contains("1 行损坏记录", result);
    }

    [Fact]
    public void 只读取logs目录下的文件_同名文件不会越界读取()
    {
        // 在配置目录根部（logs/ 之外）放一个同名文件，内含特殊标记；查询只能读到 logs/ 下的内容
        var decoyPath = Path.Combine(_directory, $"audit-{_today:yyyyMMdd}.log");
        File.WriteAllText(decoyPath, "{\"ts\":\"x\",\"event\":\"write\",\"database\":\"DECOY\"}");

        _logger.LogWrite("RealDb", "sqlite", "execute_non_query", committed: true, affectedRows: 1, sql: "INSERT INTO t VALUES (1)");

        var text = Query();

        Assert.Contains("RealDb", text);
        Assert.DoesNotContain("DECOY", text);
        Assert.True(File.Exists(decoyPath));   // 且不会因为查询被改动
    }
}
