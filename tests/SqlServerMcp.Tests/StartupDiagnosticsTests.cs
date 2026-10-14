using SqlServerMcp.Configuration;
using SqlServerMcp.Services;
using Xunit;

namespace SqlServerMcp.Tests;

/// <summary>
/// 启动自检测试：配置校验问题清单（纯函数）与连通性预检结果。
/// 全部用例不依赖真实数据库服务（SQLite 用临时文件；失败路径用不存在的目录/端口）。
/// </summary>
public sealed class StartupDiagnosticsTests
{
    private static readonly DatabaseProviderFactory Factory = new();

    private static readonly IReadOnlyCollection<string> WriteCapableTypes = Factory.SupportedTypes
        .Where(type => Factory.GetProvider(type).SupportsWrite)
        .ToList();

    private static SqlServerOptions CreateOptions(
        Action<SqlServerOptions>? configure = null)
    {
        var options = new SqlServerOptions
        {
            Databases = new Dictionary<string, DatabaseEntry>
            {
                ["Db"] = new DatabaseEntry
                {
                    Type = "sqlite",
                    ConnectionString = "Data Source=:memory:;",
                    Description = "测试库"
                }
            }
        };

        configure?.Invoke(options);
        return options;
    }

    private static IReadOnlyList<string> Validate(SqlServerOptions options) =>
        StartupDiagnostics.ValidateOptions(options, Factory.SupportedTypes, WriteCapableTypes);

    [Fact]
    public void 校验_合法配置无问题()
    {
        Assert.Empty(Validate(CreateOptions()));
    }

    [Fact]
    public void 校验_无授权数据库时提示()
    {
        var issues = Validate(CreateOptions(options => options.Databases.Clear()));

        Assert.Contains(issues, issue => issue.Contains("Databases 为空"));
    }

    [Fact]
    public void 校验_别名仅大小写不同时提示无法启动()
    {
        var issues = Validate(CreateOptions(options =>
        {
            options.Databases["DB"] = new DatabaseEntry { Type = "sqlite", ConnectionString = "Data Source=:memory:;" };
        }));

        Assert.Contains(issues, issue => issue.Contains("仅大小写不同"));
    }

    [Fact]
    public void 校验_连接串为空时提示()
    {
        var issues = Validate(CreateOptions(options =>
        {
            options.Databases["Db"].ConnectionString = "   ";
        }));

        Assert.Contains(issues, issue => issue.Contains("ConnectionString 为空"));
    }

    [Fact]
    public void 校验_类型非法时提示并列可用值()
    {
        var issues = Validate(CreateOptions(options =>
        {
            options.Databases["Db"].Type = "oracle";
        }));

        var issue = Assert.Single(issues, item => item.Contains("不受支持"));
        Assert.Contains("oracle", issue);
        Assert.Contains("mysql", issue);
        Assert.Contains("sqlite", issue);
    }

    [Fact]
    public void 校验_类型留空视为sqlserver不报错()
    {
        // 兼容旧配置：Type 缺省为 sqlserver，不应被判定为非法
        var issues = Validate(CreateOptions(options =>
        {
            options.Databases["Db"].Type = string.Empty;
        }));

        Assert.Empty(issues);
    }

    [Fact]
    public void 校验_超时非正数时提示()
    {
        var issues = Validate(CreateOptions(options => options.CommandTimeoutSeconds = 0));

        Assert.Contains(issues, issue => issue.Contains("CommandTimeoutSeconds"));
    }

    [Fact]
    public void 校验_保留天数为负数时提示()
    {
        var issues = Validate(CreateOptions(options => options.AuditRetentionDays = -1));

        Assert.Contains(issues, issue => issue.Contains("AuditRetentionDays"));
    }

    [Fact]
    public void 校验_黑名单为空时提示()
    {
        var issues = Validate(CreateOptions(options => options.DangerousKeywords.Clear()));

        Assert.Contains(issues, issue => issue.Contains("DangerousKeywords 为空"));
    }

    [Fact]
    public void 校验_开启写但无可写类型时提示矛盾()
    {
        var issues = StartupDiagnostics.ValidateOptions(
            CreateOptions(options => options.AllowWrite = true),
            Factory.SupportedTypes,
            writeCapableTypes: Array.Empty<string>());

        Assert.Contains(issues, issue => issue.Contains("没有任何已授权数据库的类型支持写操作"));
    }

    [Fact]
    public void 校验_多个问题一次性汇总()
    {
        var issues = Validate(CreateOptions(options =>
        {
            options.CommandTimeoutSeconds = -3;
            options.AuditRetentionDays = -2;
            options.Databases["Bad"] = new DatabaseEntry { Type = "oracle", ConnectionString = string.Empty };
        }));

        Assert.True(issues.Count >= 4, $"预期至少 4 条问题，实际 {issues.Count} 条：{string.Join(" | ", issues)}");
    }

    [Fact]
    public async Task 连通性预检_SQLite临时库连接成功()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sqlservermcp-conn-{Guid.NewGuid():N}.db");
        try
        {
            // Pooling=False：SQLite 连接池会保留文件句柄，导致测试结束时无法删除临时库文件
            var options = CreateOptions(config => config.Databases["Db"].ConnectionString = $"Data Source={dbPath};Pooling=False;");

            var results = await StartupDiagnostics.CheckConnectivityAsync(options, Factory);

            var result = Assert.Single(results);
            Assert.True(result.Success, result.Error);
            Assert.Equal("Db", result.Database);
            Assert.Null(result.Error);
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 连通性预检_路径不可用时返回失败原因()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), "sqlservermcp-conn-missing-" + Guid.NewGuid().ToString("N"));
        var options = CreateOptions(config =>
            config.Databases["Db"].ConnectionString = $"Data Source={Path.Combine(missingDirectory, "db.file")};");

        var results = await StartupDiagnostics.CheckConnectivityAsync(options, Factory);

        var result = Assert.Single(results);
        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task 连通性预检_类型不受支持时返回失败而不抛异常()
    {
        var options = CreateOptions(config => config.Databases["Db"].Type = "oracle");

        var results = await StartupDiagnostics.CheckConnectivityAsync(options, Factory);

        var result = Assert.Single(results);
        Assert.False(result.Success);
        Assert.Contains("类型不受支持", result.Error);
    }

    [Fact]
    public async Task 连通性预检_覆盖所有库()
    {
        var options = CreateOptions(config =>
        {
            config.Databases["Second"] = new DatabaseEntry { Type = "sqlite", ConnectionString = "Data Source=:memory:;" };
            config.Databases["Third"] = new DatabaseEntry { Type = "oracle", ConnectionString = "x" };
        });

        var results = await StartupDiagnostics.CheckConnectivityAsync(options, Factory);

        Assert.Equal(3, results.Count);
        Assert.Equal(new[] { "Db", "Second", "Third" }, results.Select(item => item.Database).ToArray());
    }
}
