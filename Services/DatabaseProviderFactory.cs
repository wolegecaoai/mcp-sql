using ModelContextProtocol;
using SqlServerMcp.Services.Providers;

namespace SqlServerMcp.Services;

/// <summary>
/// 按数据库类型（配置 Type 字段）创建并缓存 Provider。Provider 为无状态单例，可安全复用。
/// </summary>
public sealed class DatabaseProviderFactory
{
    private readonly Dictionary<string, IDatabaseProvider> _providers;

    public DatabaseProviderFactory()
    {
        var providers = new IDatabaseProvider[]
        {
            new SqlServerProvider(),
            new MySqlProvider(),
            new PostgreSqlProvider(),
            new SqliteProvider()
        };

        _providers = new Dictionary<string, IDatabaseProvider>(providers.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            _providers[provider.ProviderId] = provider;
        }
    }

    /// <summary>所有支持的数据库类型标识。</summary>
    public IReadOnlyCollection<string> SupportedTypes => _providers.Keys;

    /// <summary>按类型标识获取 Provider；type 为空时缺省为 sqlserver（保证旧配置兼容）。</summary>
    public IDatabaseProvider GetProvider(string? type)
    {
        var id = string.IsNullOrWhiteSpace(type) ? "sqlserver" : type.Trim();

        if (_providers.TryGetValue(id, out var provider))
        {
            return provider;
        }

        throw new McpException(
            $"不支持的数据库类型「{type}」。支持的类型：{string.Join("、", _providers.Keys)}。");
    }
}
