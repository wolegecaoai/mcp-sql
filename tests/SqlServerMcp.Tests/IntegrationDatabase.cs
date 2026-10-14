using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlServerMcp.Configuration;
using SqlServerMcp.Services;

namespace SqlServerMcp.Tests;

/// <summary>
/// 真实数据库集成测试的公共夹具：连接串由**环境变量**提供（不落任何凭据到仓库），
/// 缺失时抛出中文说明而非静默跳过——静默跳过会制造「假绿」。
/// <para>
/// 本地运行：<c>docker compose -f docker-compose.test.yml up -d</c>，再设置对应环境变量
/// （连接串示例见 docker-compose.test.yml）；CI 中由 workflow 的 services 容器注入。
/// </para>
/// </summary>
internal sealed class IntegrationDatabase : IDisposable
{
    internal const string Alias = "ItDb";

    internal static readonly SqlStatementType[] DmlTypes =
        { SqlStatementType.Insert, SqlStatementType.Update, SqlStatementType.Delete };

    internal static readonly SqlStatementType[] DdlTypes = { SqlStatementType.Ddl };

    private readonly string _logDirectory;

    internal DatabaseService Service { get; }

    internal IntegrationDatabase(string environmentVariable, string providerType, string providerHint)
    {
        var connectionString = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"集成测试需要环境变量 {environmentVariable} 指向可用的 {providerHint} 实例，当前为空。\n" +
                "本地运行：docker compose -f docker-compose.test.yml up -d，随后按文件内注释设置该环境变量；\n" +
                "CI 运行：由 .github/workflows/ci.yml 的 services 容器自动注入。\n" +
                "如需只跑无依赖测试，请使用默认过滤（Category!=Integration）。");
        }

        _logDirectory = Path.Combine(Path.GetTempPath(), "sqlservermcp-itlog-" + Guid.NewGuid().ToString("N"));

        var options = new SqlServerOptions
        {
            Databases = new Dictionary<string, DatabaseEntry>
            {
                [Alias] = new DatabaseEntry
                {
                    Type = providerType,
                    ConnectionString = connectionString.Trim(),
                    Description = "集成测试库"
                }
            }
        };

        Service = new DatabaseService(
            Options.Create(options),
            new DatabaseProviderFactory(),
            new SqlGuardService(),
            new AuditLogger(_logDirectory, NullLogger<AuditLogger>.Instance),
            NullLogger<DatabaseService>.Instance);
    }

    /// <summary>生成唯一表名（≤30 字符，兼容 MySQL 64 / PostgreSQL 63 的标识符上限）。</summary>
    internal static string UniqueTableName(string prefix) =>
        $"{prefix}_{DateTime.UtcNow:HHmmss}_{Guid.NewGuid():N}"[..26];

    /// <summary>忽略异常的清理（DROP TABLE IF EXISTS），避免清理失败掩盖真实断言。</summary>
    internal async Task TryDropTableAsync(string tableName)
    {
        try
        {
            await Service.ExecuteInTransactionAsync(
                Alias, $"DROP TABLE IF EXISTS {tableName}", commit: true, DdlTypes);
        }
        catch
        {
            // 清理失败不影响测试结论（表名唯一，不会污染后续运行）
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_logDirectory))
            {
                Directory.Delete(_logDirectory, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响测试结果
        }
    }
}
