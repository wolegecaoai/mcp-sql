using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using SqlServerMcp.Configuration;

namespace SqlServerMcp.Services;

/// <summary>一次查询的结果集。</summary>
/// <param name="Rows">结果行（列名为键，重复列名自动追加序号后缀）。</param>
/// <param name="Truncated">结果是否因超过行数上限被截断。</param>
/// <param name="ColumnCount">列数。</param>
/// <param name="ColumnNames">去重后的列名顺序（导出格式化为空结果集时也需要表头）。</param>
public record QueryResult(
    List<Dictionary<string, object?>> Rows,
    bool Truncated,
    int ColumnCount,
    IReadOnlyList<string>? ColumnNames = null,
    int SkippedRows = 0,
    bool TruncatedBySize = false,
    int RequestedOffset = 0)
{
    /// <summary>裸数组形态（历史输出；新调用方应使用 <see cref="ResponseFormatter"/> 的信封）。</summary>
    public string ToJson() => JsonSerializer.Serialize(Rows, DatabaseService.SerializerOptions);
}

/// <summary>
/// 参数化查询的输入参数（供工具层接收 JSON 数组，如 <c>[{"name":"age","value":30}]</c>）。
/// 占位符统一用 <c>@name</c>（SQL Server / MySQL / PostgreSQL / SQLite 的 .NET 驱动均支持）。
/// <see cref="Value"/> 在经 MCP SDK 反序列化后可能为 <see cref="System.Text.Json.JsonElement"/>，
/// 由 <see cref="DatabaseService"/> 在绑定参数时归一化为 CLR 值。
/// </summary>
public sealed record SqlParameterInput(string Name, object? Value);

/// <summary>数据库连通性与服务器信息（test_connection 与 list_databases 共用）。</summary>
public sealed record ServerInfo(
    string Database,
    string ProviderId,
    string DisplayName,
    object? ServerVersion,
    object? CurrentDatabase,
    long LatencyMs);

/// <summary>
/// 数据库访问核心服务：授权库白名单解析、Provider 路由、通用查询执行（DbConnection 抽象）、事务化写操作。
/// 数据库类型差异全部收敛在各 <see cref="IDatabaseProvider"/> 实现中。
/// </summary>
public class DatabaseService
{
    /// <summary>JSON 序列化选项：紧凑输出、不过度转义非 ASCII（中文可读）。</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly SqlServerOptions _options;
    private readonly DatabaseProviderFactory _providerFactory;
    private readonly SqlGuardService _sqlGuard;
    private readonly AuditLogger _auditLogger;
    private readonly ILogger<DatabaseService> _logger;

    /// <summary>大小写不敏感的授权库查找字典（N1：配置中同时存在 Db/db 键时行为无歧义）。</summary>
    private readonly Dictionary<string, DatabaseEntry> _databases;

    /// <summary>按 Provider 缓存的合并黑名单（M6；MI-4：并发安全——单例服务可能被并发首请求写入）。
    /// 注：<c>IOptions</c> 是启动时快照，配置热更新本就不会影响本服务，缓存语义与之保持一致。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<string>> _effectiveKeywords =
        new(StringComparer.OrdinalIgnoreCase);

    public DatabaseService(
        IOptions<SqlServerOptions> options,
        DatabaseProviderFactory providerFactory,
        SqlGuardService sqlGuard,
        AuditLogger auditLogger,
        ILogger<DatabaseService> logger)
    {
        _options = options.Value;
        _providerFactory = providerFactory;
        _sqlGuard = sqlGuard;
        _auditLogger = auditLogger;
        _logger = logger;

        try
        {
            _databases = new Dictionary<string, DatabaseEntry>(_options.Databases, StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException ex)
        {
            // 大小写不敏感比较器遇重复键时抛 ArgumentException，转为可操作的中文提示（M1）
            throw new McpException(
                "appsettings.json 的 SqlServer:Databases 中存在仅大小写不同的重复别名，请去重后重启 MCP Server。", ex);
        }
    }

    /// <summary>所有已授权数据库别名（忽略大小写排序）。</summary>
    public IReadOnlyList<string> GetDatabaseNames() =>
        _databases.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>授权库摘要列表（名称 / 类型标识 / 说明），供 list_databases 工具使用。</summary>
    public IReadOnlyList<(string Name, string Type, string Description)> GetDatabaseList() =>
        _databases
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => (
                Name: pair.Key,
                Type: string.IsNullOrWhiteSpace(pair.Value.Type) ? "sqlserver" : pair.Value.Type,
                Description: pair.Value.Description ?? string.Empty))
            .ToList();

    /// <summary>
    /// 按**数据库类型标识**（配置里的 Type 字段）获取 Provider，而不是按别名——
    /// 注意与 <see cref="GetProvider(string)"/> 区分：后者按别名解析（未授权会抛错）。
    /// 供 list_databases 富化等「只知类型、不知别名」的场景使用。
    /// </summary>
    public IDatabaseProvider GetProviderByType(string? type) => _providerFactory.GetProvider(type);

    /// <summary>按别名解析授权数据库（大小写不敏感），未授权返回 false。</summary>
    public bool TryGetDatabase(string database, [MaybeNullWhen(false)] out DatabaseEntry entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(database))
        {
            return false;
        }

        return _databases.TryGetValue(database, out entry);
    }

    /// <summary>按别名解析授权数据库，未授权时抛出带可用库列表的中文错误。</summary>
    public DatabaseEntry ResolveDatabase(string database)
    {
        if (TryGetDatabase(database, out var entry))
        {
            return entry;
        }

        var available = GetDatabaseNames();
        throw new McpException(available.Count == 0
            ? "尚未在 appsettings.json 的 SqlServer:Databases 中配置任何数据库，请先添加授权数据库。"
            : $"未授权的数据库「{database}」。可用数据库：{string.Join("、", available)}。");
    }

    /// <summary>按别名解析并返回对应数据库类型的 Provider。</summary>
    public IDatabaseProvider GetProvider(string database)
    {
        var entry = ResolveDatabase(database);
        return _providerFactory.GetProvider(entry.Type);
    }

    /// <summary>合并全局黑名单与目标库 Provider 的附加黑名单（Extra），按 Provider 缓存结果（M6）。</summary>
    public IReadOnlyList<string> GetEffectiveDangerousKeywords(string database)
    {
        var provider = GetProvider(database);
        return _effectiveKeywords.GetOrAdd(provider.ProviderId, _ =>
            provider.ExtraDangerousKeywords.Count == 0
                ? _options.DangerousKeywords
                : _options.DangerousKeywords.Concat(provider.ExtraDangerousKeywords).ToList());
    }

    /// <summary>
    /// 写入路径的统一校验（I2 守卫下沉 / CR-1 例程拦截 / MI-3 拒绝入审计）：
    /// 单语句 → 黑名单 → 例程创建 → 语句类型白名单。工具层与写入执行入口共用同一实现，
    /// 任何调用方（含未来新增工具）都无法绕过；被拒绝的语句一律留痕审计。
    /// </summary>
    /// <returns>语句分类结果（供调用方判断是否需要全表影响警告等）。</returns>
    public SqlStatementType ValidateWriteStatement(
        string database,
        string sql,
        IReadOnlyCollection<SqlStatementType> allowedTypes,
        string toolName)
    {
        var provider = GetProvider(database);
        try
        {
            _sqlGuard.AssertSingleStatement(sql, toolName);
            _sqlGuard.AssertNoDangerousKeywords(sql, GetEffectiveDangerousKeywords(database));
            _sqlGuard.AssertNoRoutineCreation(sql, provider.RoutineCreationPattern, provider.DisplayName);

            var statementType = _sqlGuard.ClassifyStatement(sql);
            if (!allowedTypes.Contains(statementType))
            {
                throw new McpException(
                    $"不支持执行 {SqlGuardService.DescribeType(statementType)} 类型的语句，" +
                    $"允许的类型：{string.Join("、", allowedTypes.Select(SqlGuardService.DescribeType))}。");
            }

            return statementType;
        }
        catch (McpException ex)
        {
            _auditLogger.LogGuardRejected(database, toolName, ex.Message, sql);
            throw;
        }
    }

    /// <summary>只读查询的守卫校验（不含执行）——供工具层在审计上下文中先行校验（MI-2）。</summary>
    public void AssertReadOnlyQuery(string database, string sql, string toolName = "execute_query") =>
        _sqlGuard.AssertReadOnly(sql, GetEffectiveDangerousKeywords(database), toolName);

    /// <summary>
    /// 执行只读查询（守卫下沉，M12）：内部完成单语句 / 分类 / SELECT INTO / 黑名单校验后再执行。
    /// 所有查询类工具（execute_query、export_query_result 及未来新增）复用此方法即自动获得完整只读防护，
    /// 消除"工具层忘记调 AssertReadOnly 即失守"的结构性风险。
    /// </summary>
    /// <param name="toolName">发起调用的工具名，仅用于准确的报错文案（I5）。</param>
    public async Task<QueryResult> ExecuteReadOnlyQueryAsync(
        string database, string sql, int maxRows, string toolName = "execute_query", CancellationToken ct = default)
    {
        AssertReadOnlyQuery(database, sql, toolName);
        return await ExecuteQueryAsync(database, sql, maxRows, ct: ct);
    }

    /// <summary>
    /// 只读查询（参数化入口）：占位符 @name，四类库通用。守卫仍基于 SQL 模板文本，
    /// 参数值不进入 SQL 文本，反而不易被注入绕过。offset 透传以支持分页续取。
    /// </summary>
    public async Task<QueryResult> ExecuteReadOnlyQueryAsync(
        string database, string sql, int maxRows, IEnumerable<SqlParameterInput>? parameters,
        string toolName = "execute_query", CancellationToken ct = default, int offset = 0)
    {
        AssertReadOnlyQuery(database, sql, toolName);
        return await ExecuteQueryAsync(database, sql, maxRows, parameters, ct: ct, offset: offset);
    }

    /// <summary>
    /// 探测连通性与服务器信息（版本、当前库）——test_connection 与 list_databases 共用，
    /// 避免两处各写一遍版本探测。执行的是 Provider 自身生成的 SQL，无注入面。
    /// 失败抛已翻译的 <see cref="McpException"/>，由调用方决定整体失败还是逐库报告。
    /// </summary>
    public async Task<ServerInfo> GetServerInfoAsync(
        string database, int? commandTimeoutSeconds = null, CancellationToken ct = default)
    {
        var provider = GetProvider(database);
        var startedAt = System.Diagnostics.Stopwatch.StartNew();

        // 超时必须覆盖**建连**阶段：CommandTimeout 只约束命令执行，而驱动的 Connect Timeout 默认约 15 秒，
        // 仅靠它会破坏「单库限时 N 秒」的承诺（I4）
        using var timeoutSource = commandTimeoutSeconds is > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;

        if (timeoutSource is not null)
        {
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(commandTimeoutSeconds!.Value));
        }

        var token = timeoutSource?.Token ?? ct;

        try
        {
            var result = await ExecuteQueryAsync(
                database, provider.BuildServerVersionSql(), maxRows: 1, ct: token,
                commandTimeoutSeconds: commandTimeoutSeconds ?? 0);
            startedAt.Stop();

            var row = result.Rows.Count > 0 ? result.Rows[0] : new Dictionary<string, object?>();

            return new ServerInfo(
                database,
                provider.ProviderId,
                provider.DisplayName,
                row.TryGetValue("server_version", out var version) ? version : null,
                row.TryGetValue("current_database", out var current) ? current : null,
                startedAt.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (timeoutSource is not null && !ct.IsCancellationRequested)
        {
            // 本库超时（非外部取消）：翻译成中文交由调用方逐库报告
            throw new McpException(
                $"数据库「{database}」连通性探测超时（超过 {commandTimeoutSeconds} 秒）。" +
                "请检查主机可达性、端口与防火墙，或适当增大 SqlServer:CommandTimeoutSeconds。",
                new TimeoutException($"探测「{database}」超时"));
        }
    }

    /// <summary>
    /// 批量探测所有授权库的服务器信息（list_databases 的 includeServerInfo 用）。
    /// **并行 + 单库短超时**：串行时不可达库会各等一个命令超时（默认 30 秒），N 个库能把调用拖到几分钟；
    /// 单个库失败只记录错误，不影响其他库。
    /// </summary>
    public async Task<IReadOnlyList<(string Database, ServerInfo? Info, string? Error)>> GetAllServerInfoAsync(
        int commandTimeoutSeconds = 5, CancellationToken ct = default)
    {
        var tasks = _databases.Keys
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(async name =>
            {
                try
                {
                    var info = await GetServerInfoAsync(name, commandTimeoutSeconds, ct);
                    return (Database: name, Info: (ServerInfo?)info, Error: (string?)null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;   // 外部取消应整体传播，而不是被当作单库失败
                }
                catch (OperationCanceledException)
                {
                    return (Database: name, Info: (ServerInfo?)null, Error: (string?)"探测被取消或超时");
                }
                catch (McpException ex)
                {
                    return (Database: name, Info: (ServerInfo?)null, Error: (string?)ex.Message);
                }
                catch (Exception ex)
                {
                    // 未预期异常同样只影响该条目，否则「单库失败隔离」对异常类型不成立（I5）；错误文案脱敏（L3）
                    return (Database: name, Info: (ServerInfo?)null, Error: (string?)$"未预期的错误：{SecretRedactor.Redact(ex.Message)}");
                }
            })
            .ToList();

        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// 打开到指定授权数据库的连接，连接失败时由 Provider 翻译为可操作的中文错误。
    /// internal（IM-1）：裸连接是"无守卫执行任意 SQL"的逃逸口，仅在程序集内部与测试（InternalsVisibleTo）可用。
    /// </summary>
    internal async Task<DbConnection> OpenConnectionAsync(string database, CancellationToken ct = default)
    {
        var entry = ResolveDatabase(database);
        var provider = _providerFactory.GetProvider(entry.Type);
        if (string.IsNullOrWhiteSpace(entry.ConnectionString))
        {
            throw new McpException($"数据库「{database}」未配置连接字符串（ConnectionString）。");
        }

        // 连接创建（连接串解析）与打开均纳入 try：格式错误经 TranslateError 转中文，避免回显连接串原文（M8）
        DbConnection? connection = null;
        try
        {
            connection = provider.CreateConnection(entry.ConnectionString);
            await connection.OpenAsync(ct);
            _logger.LogDebug("已连接数据库 {Database}（{Provider}）", database, provider.ProviderId);
            return connection;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync();
                }
                catch
                {
                    // 释放失败时保留原始错误，避免掩盖根因
                }
            }

            // 翻译为中文后做密码脱敏，避免凭据随错误文案回显给调用方（4.6）
            throw new McpException(SecretRedactor.Redact(provider.TranslateError(ex, database)), ex);
        }
    }

    /// <summary>
    /// 执行查询（参数化入口，接收 <see cref="SqlParameterInput"/>，占位符用 @name，四类库通用）。
    /// 内部委托 <see cref="ExecuteQueryCore"/>，避免与方言 Provider 的内部 <see cref="DbParameter"/> 入口重复逻辑。
    /// 注意：本重载的 parameters 无默认值，确保「省略 parameters」的调用（如 list_views）能唯一解析到 DbParameter 重载。
    /// </summary>
    internal async Task<QueryResult> ExecuteQueryAsync(
        string database,
        string sql,
        int maxRows,
        IEnumerable<SqlParameterInput>? parameters,
        CancellationToken ct = default,
        int offset = 0,
        int maxChars = 0,
        int commandTimeoutSeconds = 0)
        => await ExecuteQueryCore(database, sql, maxRows, command => AddSqlParameters(command, parameters), ct, offset, maxChars, commandTimeoutSeconds);

    /// <summary>
    /// 执行查询（方言 Provider 内部入口，接收已构造的 <see cref="DbParameter"/>）。
    /// </summary>
    internal async Task<QueryResult> ExecuteQueryAsync(
        string database,
        string sql,
        int maxRows,
        IEnumerable<DbParameter>? parameters = null,
        CancellationToken ct = default,
        int offset = 0,
        int maxChars = 0,
        int commandTimeoutSeconds = 0)
        => await ExecuteQueryCore(database, sql, maxRows, command =>
        {
            if (parameters is not null)
            {
                foreach (var parameter in parameters)
                {
                    // DbParameter 实例只能隶属于一个 DbParameterCollection，
                    // 这里复制后加入，允许调用方在多次查询间复用同一组参数对象。
                    command.Parameters.Add(CopyParameter(command, parameter));
                }
            }
        }, ct, offset, maxChars, commandTimeoutSeconds);

    /// <summary>
    /// 查询执行核心（两个参数化重载共用）：打开连接、绑定参数、读取最多 maxRows 行，
    /// 超行数 / 字符预算截断，并对 Provider 返回的异常做中文翻译与密码脱敏（4.6）。
    /// internal（I1）：查询类调用必须走 <see cref="ExecuteReadOnlyQueryAsync"/>（内含只读守卫），
    /// 外部程序集无法绕过守卫直接调用本方法。测试通过 InternalsVisibleTo 访问。
    /// </summary>
    /// <param name="offset">跳过结果集的前 N 行（分页续取用；注意读取代价仍存在）。</param>
    /// <param name="maxChars">字符预算覆盖值；≤0 表示使用配置默认。</param>
    /// <param name="commandTimeoutSeconds">命令超时覆盖值；≤0 表示使用配置默认（批量探测用短超时）。</param>
    private async Task<QueryResult> ExecuteQueryCore(
        string database,
        string sql,
        int maxRows,
        Action<DbCommand> bindParameters,
        CancellationToken ct,
        int offset,
        int maxChars,
        int commandTimeoutSeconds)
    {
        var provider = GetProvider(database);
        var connection = await OpenConnectionAsync(database, ct);
        await using var _ = connection;

        // CommandTimeoutSeconds=0 在 ADO.NET 中表示永不超时，钳制为至少 1 秒（M7）
        var commandTimeout = Math.Max(1, commandTimeoutSeconds > 0 ? commandTimeoutSeconds : _options.CommandTimeoutSeconds);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = commandTimeout;
        bindParameters(command);

        var rows = new List<Dictionary<string, object?>>();
        var truncated = false;
        var truncatedBySize = false;
        var skipped = 0;
        var rowChars = 0;
        var charBudget = maxChars > 0 ? maxChars : _options.MaxResponseChars;
        var columnCount = 0;
        string[] names = Array.Empty<string>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            columnCount = reader.FieldCount;

            // 列名去重（同名列追加 _2、_3 后缀）
            names = new string[columnCount];
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columnCount; i++)
            {
                var name = reader.GetName(i);
                var unique = name;
                var suffix = 2;
                while (!used.Add(unique))
                {
                    unique = $"{name}_{suffix++}";
                }

                names[i] = unique;
            }

            while (await reader.ReadAsync(ct))
            {
                // 分页：先跳过结果集前 offset 行（代价是仍需读取这些行，故仅作为续取手段）
                if (skipped < offset)
                {
                    skipped++;
                    continue;
                }

                if (rows.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }

                var row = new Dictionary<string, object?>(columnCount, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < columnCount; i++)
                {
                    var value = await reader.IsDBNullAsync(i)
                        ? null
                        : NormalizeValue(reader.GetValue(i));

                    // 单值上限：一个超长 TEXT/BLOB 列就能单独打爆上下文，
                    // 故单值不得超过整体预算（截断处留明确标记，避免"看起来是完整数据"）
                    if (charBudget > 0 && value is string text && text.Length > charBudget)
                    {
                        value = text[..charBudget] + $"…（单值超长已截断，原始长度 {text.Length} 字符）";
                        truncatedBySize = true;
                    }

                    row[names[i]] = value;
                }

                // 字符预算闸门：至少保留一行（否则结果无意义），其后超预算即按体积截断
                var rowSize = MeasureRowChars(row);
                if (rows.Count > 0 && charBudget > 0 && rowChars + rowSize > charBudget)
                {
                    truncated = true;
                    truncatedBySize = true;
                    break;
                }

                rowChars += rowSize;
                rows.Add(row);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 翻译为中文后做密码脱敏，避免凭据随错误文案回显给调用方 / 审计日志（4.6）
            throw new McpException(SecretRedactor.Redact(provider.TranslateError(ex, database)), ex);
        }

        return new QueryResult(rows, truncated, columnCount, names, skipped, truncatedBySize, offset);
    }

    /// <summary>
    /// 一行的实际 JSON 字符数（含转义：引号/控制字符会被转义成 2–6 倍，按原始长度估算会显著低估预算）。
    /// 行已物化，多序列化一次换取预算准确，代价可接受。
    /// </summary>
    private static int MeasureRowChars(Dictionary<string, object?> row) =>
        JsonSerializer.Serialize(row, SerializerOptions).Length;

    /// <summary>
    /// 在显式事务中执行写语句（INSERT/UPDATE/DELETE/DDL）。占位符 @name，四类库通用。
    /// 见 <see cref="ExecuteWriteCore"/> 的语义说明（含密码脱敏）。
    /// </summary>
    /// <param name="allowedTypes">允许执行的语句类型白名单（必填，避免漏传即放行——IM-2）。</param>
    public async Task<int> ExecuteInTransactionAsync(
        string database,
        string sql,
        bool commit,
        IReadOnlyCollection<SqlStatementType> allowedTypes,
        IEnumerable<SqlParameterInput>? parameters,
        CancellationToken ct = default)
        => await ExecuteWriteCore(database, sql, commit, allowedTypes, command => AddSqlParameters(command, parameters), ct);

    /// <summary>
    /// 在显式事务中执行写语句（方言 Provider 内部入口，无参数化）。
    /// </summary>
    public async Task<int> ExecuteInTransactionAsync(
        string database,
        string sql,
        bool commit,
        IReadOnlyCollection<SqlStatementType> allowedTypes,
        CancellationToken ct = default)
        => await ExecuteWriteCore(database, sql, commit, allowedTypes, _ => { }, ct);

    /// <summary>
    /// 写操作执行核心（两个重载共用）：commit=false 时执行后回滚（演练模式），commit=true 时提交。
    /// 守卫下沉（I2）：单语句 / 黑名单 / 语句分类在此二次校验，作为工具层之外的结构性兜底——
    /// 任何新的写入口即使忘记校验，也无法绕过安全机制。建连失败与所有异常均做密码脱敏（4.6）后留痕审计。
    /// </summary>
    /// <param name="allowedTypes">允许执行的语句类型白名单（必填，避免漏传即放行——IM-2）。</param>
    private async Task<int> ExecuteWriteCore(
        string database,
        string sql,
        bool commit,
        IReadOnlyCollection<SqlStatementType> allowedTypes,
        Action<DbCommand> bindParameters,
        CancellationToken ct)
    {
        var provider = GetProvider(database);

        // 服务层兜底校验（I2/MI-3）：与工具层共用同一实现（含 CR-1 例程拦截），拒绝入审计。
        // 工具层已校验通过时此处为幂等重复检查；任何绕过工具层的调用方都会被拦下。
        var statementType = ValidateWriteStatement(database, sql, allowedTypes, "execute_write");

        // DDL 不可事务化的方言（MySQL InnoDB 隐式提交）：走 autocommit 直执行。
        // 若仍开事务，DDL 隐式提交后 CommitAsync 会抛 InvalidOperationException，
        // 造成"已提交却报错"的误导（I3）；演练模式对该类语句无意义，fail-closed 拒绝。
        var useTransaction = provider.SupportsTransactionalDdl || statementType != SqlStatementType.Ddl;
        if (!useTransaction && !commit)
        {
            // 该判定必须在建立连接前完成：一次注定不执行的演练不应触发任何网络连接
            throw new McpException(
                $"当前数据库类型（{provider.DisplayName}）的 DDL 会立即提交、无法回滚，演练模式不可用。" +
                "请人工确认语句无误后以 confirm=true 执行。");
        }

        DbConnection connection;
        try
        {
            connection = await OpenConnectionAsync(database, ct);
        }
        catch (McpException ex)
        {
            // 建连失败同属写失败，需留痕审计（MI-1）；错误文案脱敏
            _auditLogger.LogWriteFailed(database, provider.ProviderId, "execute_write", SecretRedactor.Redact(ex.Message), sql);
            throw;
        }

        await using var _ = connection;

        if (!useTransaction)
        {
            try
            {
                await using var autoCommitCommand = connection.CreateCommand();
                autoCommitCommand.CommandText = sql;
                autoCommitCommand.CommandTimeout = Math.Max(1, _options.CommandTimeoutSeconds);
                bindParameters(autoCommitCommand);
                var autoCommitAffected = await autoCommitCommand.ExecuteNonQueryAsync(ct);

                _logger.LogInformation(
                    "数据库 {Database} 执行写操作（autocommit 已提交），影响行数 {Affected}",
                    database, autoCommitAffected);

                return autoCommitAffected;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _auditLogger.LogWriteFailed(database, provider.ProviderId, "execute_write", SecretRedactor.Redact(ex.Message), sql);
                throw new McpException(SecretRedactor.Redact(provider.TranslateError(ex, database)), ex);
            }
        }

        // BeginTransaction 纳入 try（M4）：开事务失败同样走审计 + 中文翻译
        DbTransaction transaction;
        try
        {
            transaction = await connection.BeginTransactionAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _auditLogger.LogWriteFailed(database, provider.ProviderId, "execute_write", SecretRedactor.Redact(ex.Message), sql);
            throw new McpException(SecretRedactor.Redact(provider.TranslateError(ex, database)), ex);
        }

        await using var __ = transaction;
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.CommandTimeout = Math.Max(1, _options.CommandTimeoutSeconds);
            bindParameters(command);

            var affected = await command.ExecuteNonQueryAsync(ct);

            if (commit)
            {
                await transaction.CommitAsync(ct);
            }
            else
            {
                await transaction.RollbackAsync(ct);
            }

            _logger.LogInformation(
                "数据库 {Database} 执行写操作（{Mode}），影响行数 {Affected}",
                database,
                commit ? "已提交" : "演练模式已回滚",
                affected);

            return affected;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // 回滚失败时保留原始异常即可
            }

            _auditLogger.LogWriteFailed(database, provider.ProviderId, "execute_write", SecretRedactor.Redact(ex.Message), sql);
            throw new McpException(SecretRedactor.Redact(provider.TranslateError(ex, database)), ex);
        }
    }

    private static DbParameter CopyParameter(DbCommand command, DbParameter source)
    {
        var copy = command.CreateParameter();
        copy.ParameterName = source.ParameterName;
        copy.Direction = source.Direction;
        copy.Size = source.Size;
        copy.IsNullable = source.IsNullable;
        copy.Value = source.Value ?? DBNull.Value;
        return copy;
    }

    /// <summary>
    /// 把 <see cref="SqlParameterInput"/> 列表绑定到命令（按 @name 占位符）。
    /// 值经 <see cref="ToDbValue"/> 归一化，确保经 MCP SDK 反序列化得到的 <see cref="System.Text.Json.JsonElement"/> 也正确落地。
    /// </summary>
    private static void AddSqlParameters(DbCommand command, IEnumerable<SqlParameterInput>? parameters)
    {
        if (parameters is null)
        {
            return;
        }

        // L5：参数个数上限，避免把上千个参数直接丢给驱动（各驱动行为不一）
        const int MaxParameterCount = 500;

        // 同名参数后者覆盖前者：规避 SqlClient 对重复名的 ArgumentException，同时让四类库行为一致
        var unique = new Dictionary<string, SqlParameterInput>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name))
            {
                throw new McpException("参数化查询失败：存在参数名为空的参数。");
            }

            unique[parameter.Name] = parameter;
        }

        if (unique.Count > MaxParameterCount)
        {
            throw new McpException($"参数化查询失败：参数个数（{unique.Count}）超过上限 {MaxParameterCount}。");
        }

        foreach (var parameter in unique.Values)
        {
            var dbParameter = command.CreateParameter();
            dbParameter.ParameterName = parameter.Name;
            dbParameter.Value = ToDbValue(parameter.Value);
            command.Parameters.Add(dbParameter);
        }
    }

    /// <summary>
    /// 将参数值归一化为 ADO.NET 可用的 CLR 值（JsonElement 由 <see cref="JeToDbValue"/> 具体化）。
    /// </summary>
    private static object? ToDbValue(object? value) => value switch
    {
        null => DBNull.Value,
        JsonElement je => JeToDbValue(je),
        _ => value
    };

    private static object? JeToDbValue(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.Null => DBNull.Value,
        JsonValueKind.String => je.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        // 整数优先 long（避免小整数被当成 double 导致参数类型偏差），否则 double
        JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
        // 数组 / 对象作为 JSON 文本传入（调用方按需用 JSON 函数解析）
        _ => je.GetRawText()
    };

    private static object? NormalizeValue(object value) => value switch
    {
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.fff"),
        DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd"),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss zzz"),
        TimeSpan timeSpan => timeSpan.ToString(),
        byte[] bytes => Convert.ToBase64String(bytes),
        Guid guid => guid.ToString(),
        // 非有限浮点数无法被 JSON 序列化，转为字符串保留语义（I9）
        double.NaN => "NaN",
        double.PositiveInfinity => "Infinity",
        double.NegativeInfinity => "-Infinity",
        float.NaN => "NaN",
        float.PositiveInfinity => "Infinity",
        float.NegativeInfinity => "-Infinity",
        _ => value
    };
}
