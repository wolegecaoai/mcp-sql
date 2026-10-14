using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlServerMcp.Configuration;

namespace SqlServerMcp.Services;

/// <summary>
/// 启动自检：配置合法性校验（纯函数，可测试）与可选连通性预检，附带审计日志保留清理。
/// <para>
/// **设计原则：只告警不阻断。** 本服务是 stdio MCP Server——若启动即退出，客户端侧几乎拿不到任何
/// 诊断信息（stdout 是协议通道，错误日志需要客户端配合才能看到）。因此除配置系统自身解析失败外，
/// 一切配置问题都降级为 stderr 警告，让服务照常起来、问题通过 <c>query_audit_log</c> 与连接错误反馈。
/// </para>
/// </summary>
public sealed class StartupDiagnostics
{
    private readonly IOptions<SqlServerOptions> _options;
    private readonly DatabaseProviderFactory _providerFactory;
    private readonly AuditLogger _auditLogger;
    private readonly string _configDirectory;
    private readonly ILogger<StartupDiagnostics> _logger;

    public StartupDiagnostics(
        IOptions<SqlServerOptions> options,
        DatabaseProviderFactory providerFactory,
        AuditLogger auditLogger,
        string configDirectory,
        ILogger<StartupDiagnostics> logger)
    {
        _options = options;
        _providerFactory = providerFactory;
        _auditLogger = auditLogger;
        _configDirectory = configDirectory;
        _logger = logger;
    }

    /// <summary>启动流程：配置校验 → 审计日志保留清理 → 可选连通性预检。全部失败均不抛出。</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var options = _options.Value;

        // 配置目录是排查「改了配置却不生效」「容器里没挂载配置」的第一线索，启动即输出
        _logger.LogInformation("配置目录：{Directory}（审计日志写入 {Logs}）",
            _configDirectory, Path.Combine(_configDirectory, "logs"));

        if (!File.Exists(Path.Combine(_configDirectory, "appsettings.json")))
        {
            _logger.LogWarning(
                "配置目录下没有 appsettings.json：当前按空配置运行（工具会提示缺少授权数据库）。" +
                "可复制 appsettings.example.json 为 appsettings.json 并填写连接串。");
        }

        var issues = ValidateOptions(options, _providerFactory.SupportedTypes, GetWriteCapableTypes());
        if (issues.Count == 0)
        {
            _logger.LogInformation("启动自检通过：{Count} 个授权数据库，写操作{WriteState}。",
                options.Databases.Count, options.AllowWrite ? "已启用" : "已禁用");
        }
        else
        {
            foreach (var issue in issues)
            {
                _logger.LogWarning("启动自检发现问题：{Issue}", issue);
            }
        }

        try
        {
            _auditLogger.PruneOldFiles(options.AuditRetentionDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "启动时清理过期审计日志失败（不影响运行）");
        }

        if (!options.StartupConnectivityCheck)
        {
            return;
        }

        _logger.LogInformation("开始连通性预检（StartupConnectivityCheck=true）……");
        foreach (var result in await CheckConnectivityAsync(options, _providerFactory, ct))
        {
            if (result.Success)
            {
                _logger.LogInformation("连通性预检通过：数据库「{Database}」（耗时 {Elapsed} 毫秒）",
                    result.Database, result.ElapsedMilliseconds);
            }
            else
            {
                _logger.LogWarning("连通性预检失败：数据库「{Database}」——{Error}",
                    result.Database, result.Error);
            }
        }
    }

    /// <summary>
    /// 校验配置合法性，返回中文问题清单（空列表表示无问题）。纯函数：不读文件、不建立连接。
    /// </summary>
    /// <param name="supportedTypes">所有受支持的 Type 值。</param>
    /// <param name="writeCapableTypes">支持写的 Type 值；为 null 时跳过「写开关与能力矛盾」检查。</param>
    public static IReadOnlyList<string> ValidateOptions(
        SqlServerOptions options,
        IReadOnlyCollection<string> supportedTypes,
        IReadOnlyCollection<string>? writeCapableTypes = null)
    {
        var issues = new List<string>();

        // JSON 显式写 "Databases": null 时绑定结果为 null，此处兜底避免自检自身抛异常
        if (options.Databases is null)
        {
            return new[] { "SqlServer:Databases 显式为 null：请删除该字段或改为空对象 {}。" };
        }

        if (options.Databases.Count == 0)
        {
            issues.Add("SqlServer:Databases 为空：没有任何授权数据库，所有数据工具都会拒绝执行。请在 appsettings.json 中登记至少一个库。");
        }

        // 别名仅大小写不同会让 DatabaseService 构造失败（别名不区分大小写），必须前置提示
        var duplicates = options.Databases.Keys
            .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(" / ", group))
            .ToList();

        foreach (var duplicate in duplicates)
        {
            issues.Add($"数据库别名仅大小写不同：{duplicate}。别名不区分大小写，请改成不同的名字，否则服务无法启动。");
        }

        foreach (var (name, entry) in options.Databases)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                issues.Add("存在空的数据库别名，请填写名称（该条目不会被任何工具使用）。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.ConnectionString))
            {
                issues.Add($"数据库「{name}」的 ConnectionString 为空，调用时会直接连接失败。");
            }

            // 空 Type 会被工厂缺省为 sqlserver（兼容旧配置），故只校验非空但非法的情况
            if (!string.IsNullOrWhiteSpace(entry.Type)
                && !supportedTypes.Contains(entry.Type.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                issues.Add($"数据库「{name}」的 Type「{entry.Type}」不受支持。可用值：{string.Join("、", supportedTypes.OrderBy(type => type, StringComparer.Ordinal))}。");
            }
        }

        if (options.CommandTimeoutSeconds <= 0)
        {
            issues.Add($"CommandTimeoutSeconds 应为正数，当前为 {options.CommandTimeoutSeconds}；代码会按 1 秒兜底，但建议显式配置。");
        }

        if (options.AuditRetentionDays < 0)
        {
            issues.Add($"AuditRetentionDays 不能为负数，当前为 {options.AuditRetentionDays}；负数等同 0（不清理审计日志）。");
        }

        if (options.MaxResponseChars is > 0 and < 1000)
        {
            issues.Add($"MaxResponseChars 过小（当前 {options.MaxResponseChars}）：单次查询可能只返回极少量行，建议不小于 1000，或设为 0 表示不限制。");
        }

        if (options.DangerousKeywords.Count == 0)
        {
            issues.Add("DangerousKeywords 为空：危险关键词黑名单（实例级操作、提权语句、数据外发函数）已全部失效，请确认这是有意为之。");
        }

        if (writeCapableTypes is not null && options.AllowWrite && options.Databases.Count > 0)
        {
            var writable = options.Databases.Values.Count(entry =>
                writeCapableTypes.Contains((entry.Type ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
                || (string.IsNullOrWhiteSpace(entry.Type) && writeCapableTypes.Contains("sqlserver", StringComparer.OrdinalIgnoreCase)));

            if (writable == 0)
            {
                issues.Add("AllowWrite=true，但没有任何已授权数据库的类型支持写操作：写工具会逐个拒绝。如需纯查询分析，请将 AllowWrite 设为 false。");
            }
        }

        return issues;
    }

    /// <summary>
    /// 逐个库做连通性预检（默认关闭）。每库独立超时，失败只作为结果返回、不抛出。
    /// </summary>
    public static async Task<IReadOnlyList<ConnectivityResult>> CheckConnectivityAsync(
        SqlServerOptions options,
        DatabaseProviderFactory providerFactory,
        CancellationToken ct = default)
    {
        var results = new List<ConnectivityResult>();
        var timeoutSeconds = Math.Max(1, options.CommandTimeoutSeconds);

        foreach (var (name, entry) in options.Databases)
        {
            var stopwatch = Stopwatch.StartNew();

            IDatabaseProvider provider;
            try
            {
                provider = providerFactory.GetProvider(entry.Type);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                results.Add(new ConnectivityResult(name, false, $"类型不受支持：{SecretRedactor.Redact(ex.Message)}", stopwatch.ElapsedMilliseconds));
                continue;
            }

            try
            {
                await using var connection = provider.CreateConnection(entry.ConnectionString);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                await connection.OpenAsync(timeout.Token);
                stopwatch.Stop();
                results.Add(new ConnectivityResult(name, true, null, stopwatch.ElapsedMilliseconds));
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                // 区分「本库超时」与「外部取消」：后者若也报“连接超时”会误导排查方向（M2）
                var error = ex switch
                {
                    OperationCanceledException when !ct.IsCancellationRequested =>
                        $"连接超时（超过 {timeoutSeconds} 秒）",
                    OperationCanceledException => "预检已被取消",
                    // 建连失败的错误文案做密码脱敏，避免凭据随诊断信息回显（4.6）
                    _ => SecretRedactor.Redact(provider.TranslateError(ex, name))
                };

                results.Add(new ConnectivityResult(name, false, error, stopwatch.ElapsedMilliseconds));
            }
        }

        return results;
    }

    private IReadOnlyCollection<string> GetWriteCapableTypes() =>
        _providerFactory.SupportedTypes
            .Where(type => _providerFactory.GetProvider(type).SupportsWrite)
            .ToList();
}

/// <summary>单个数据库的连通性预检结果。</summary>
/// <param name="Database">数据库别名。</param>
/// <param name="Success">是否连接成功。</param>
/// <param name="Error">失败原因（成功时为 null）。</param>
/// <param name="ElapsedMilliseconds">耗时（毫秒）。</param>
public sealed record ConnectivityResult(string Database, bool Success, string? Error, long ElapsedMilliseconds);
