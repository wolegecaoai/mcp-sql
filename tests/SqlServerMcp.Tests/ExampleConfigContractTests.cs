using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using SqlServerMcp.Configuration;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// 示例配置模板（appsettings.example.json）契约测试。
/// 该文件是 CI 与发布产物的共同依赖：模板与配置模型漂移会导致 CI 生成的配置缺字段，
/// 而模板混入真实凭据会让公开产物泄露内网信息——两者都由本测试兜住。
/// </summary>
public sealed class ExampleConfigContractTests
{
    private static readonly string ConfigPath = FindExampleConfigPath();

    private static SqlServerOptions LoadOptions()
    {
        // 与生产一致的绑定方式（Configuration Binder + "SqlServer" 节）
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(ConfigPath, optional: false)
            .Build();

        var options = configuration.GetSection(SqlServerOptions.SectionName).Get<SqlServerOptions>();
        Assert.NotNull(options);
        return options!;
    }

    private static string FindExampleConfigPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "appsettings.example.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到 appsettings.example.json：测试应在仓库目录内运行。");
    }

    [Fact]
    public void 示例配置可绑定为SqlServerOptions()
    {
        var options = LoadOptions();

        Assert.NotEmpty(options.Databases);
        Assert.NotEmpty(options.DangerousKeywords);
    }

    [Fact]
    public void 示例配置覆盖四种数据库类型()
    {
        var types = LoadOptions().Databases.Values
            .Select(entry => entry.Type)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("sqlserver", types);
        Assert.Contains("mysql", types);
        Assert.Contains("postgresql", types);
        Assert.Contains("sqlite", types);
    }

    [Fact]
    public void 示例配置每个条目都有连接串与说明()
    {
        foreach (var (name, entry) in LoadOptions().Databases)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.ConnectionString), $"「{name}」缺连接串");
            Assert.False(string.IsNullOrWhiteSpace(entry.Description), $"「{name}」缺说明");
        }
    }

    [Fact]
    public void 示例配置包含新增配置项且不含已移除的MaxRows()
    {
        var text = File.ReadAllText(ConfigPath);

        Assert.Contains("AuditRetentionDays", text);
        Assert.Contains("StartupConnectivityCheck", text);
        Assert.DoesNotContain("MaxRows", text);
    }

    [Fact]
    public void 示例配置不含真实凭据特征()
    {
        var text = File.ReadAllText(ConfigPath);

        // 真实内网主机（本项目开发环境的实际地址）不得出现在可提交模板中
        Assert.DoesNotContain("192.168.", text);

        // 不得出现连续 8 位以上数字（真实口令特征）
        Assert.DoesNotMatch(new Regex(@"\d{8,}"), text);

        // 出现口令字段（Password= / Pwd=）时必须为占位符
        foreach (Match match in Regex.Matches(text, @"(?:Password|Pwd)=([^;""]+)"))
        {
            Assert.StartsWith("your_", match.Groups[1].Value);
        }
    }

    [Fact]
    public void 示例配置的反斜杠路径说明与平台一致()
    {
        // SQLite 示例需同时给出 Windows 与 Unix 路径写法，避免 mac/Linux 用户照抄失效
        var text = File.ReadAllText(ConfigPath);

        Assert.Contains("/path/to/your.db", text);
        Assert.Contains("Data Source=", text);
    }
}
