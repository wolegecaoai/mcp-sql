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
public record QueryResult(List<Dictionary<string, object?>> Rows, bool Truncated, int ColumnCount)
{
    public string ToJson() => JsonSerializer.Serialize(Rows, DatabaseService.SerializerOptions);
}

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
    private readonly ILogger<DatabaseService> _logger;

    public DatabaseService(
        IOptions<SqlServerOptions> options,
        DatabaseProviderFactory providerFactory,
        ILogger<DatabaseService> logger)
    {
        _options = options.Value;
        _providerFactory = providerFactory;
        _logger = logger;
    }

    /// <summary>所有已授权数据库别名（忽略大小写排序）。</summary>
    public IReadOnlyList<string> GetDatabaseNames() =>
        _options.Databases.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>授权库摘要列表（名称 / 类型标识 / 说明），供 list_databases 工具使用。</summary>
    public IReadOnlyList<(string Name, string Type, string Description)> GetDatabaseList() =>
        _options.Databases
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => (
                Name: pair.Key,
                Type: string.IsNullOrWhiteSpace(pair.Value.Type) ? "sqlserver" : pair.Value.Type,
                Description: pair.Value.Description ?? string.Empty))
            .ToList();

    /// <summary>按别名解析授权数据库（大小写不敏感），未授权返回 false。</summary>
    public bool TryGetDatabase(string database, [MaybeNullWhen(false)] out DatabaseEntry entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(database))
        {
            return false;
        }

        if (_options.Databases.TryGetValue(database, out var exact))
        {
            entry = exact;
            return true;
        }

        var match = _options.Databases.FirstOrDefault(
            pair => string.Equals(pair.Key, database, StringComparison.OrdinalIgnoreCase));
        if (match.Value is null)
        {
            return false;
        }

        entry = match.Value;
        return true;
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

    /// <summary>打开到指定授权数据库的连接，连接失败时由 Provider 翻译为可操作的中文错误。</summary>
    public async Task<DbConnection> OpenConnectionAsync(string database, CancellationToken ct = default)
    {
        var entry = ResolveDatabase(database);
        var provider = _providerFactory.GetProvider(entry.Type);
        if (string.IsNullOrWhiteSpace(entry.ConnectionString))
        {
            throw new McpException($"数据库「{database}」未配置连接字符串（ConnectionString）。");
        }

        var connection = provider.CreateConnection(entry.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            _logger.LogDebug("已连接数据库 {Database}（{Provider}）", database, provider.ProviderId);
            return connection;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch
            {
                // 释放失败时保留原始连接错误，避免掩盖根因
            }

            throw new McpException(provider.TranslateError(ex, database), ex);
        }
    }

    /// <summary>
    /// 执行查询并读取最多 maxRows 行。超出上限时截断（Truncated=true），避免大结果集打爆上下文。
    /// </summary>
    public async Task<QueryResult> ExecuteQueryAsync(
        string database,
        string sql,
        int maxRows,
        IEnumerable<DbParameter>? parameters = null,
        CancellationToken ct = default)
    {
        var provider = GetProvider(database);
        var connection = await OpenConnectionAsync(database, ct);
        await using var _ = connection;

        // CommandTimeoutSeconds=0 在 ADO.NET 中表示永不超时，钳制为至少 1 秒（M7）
        var commandTimeout = Math.Max(1, _options.CommandTimeoutSeconds);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = commandTimeout;
        if (parameters is not null)
        {
            foreach (var parameter in parameters)
            {
                // DbParameter 实例只能隶属于一个 DbParameterCollection，
                // 这里复制后加入，允许调用方在多次查询间复用同一组参数对象。
                command.Parameters.Add(CopyParameter(command, parameter));
            }
        }

        var rows = new List<Dictionary<string, object?>>();
        var truncated = false;
        var columnCount = 0;
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            columnCount = reader.FieldCount;

            // 列名去重（同名列追加 _2、_3 后缀）
            var names = new string[columnCount];
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
                if (rows.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }

                var row = new Dictionary<string, object?>(columnCount, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < columnCount; i++)
                {
                    row[names[i]] = await reader.IsDBNullAsync(i)
                        ? null
                        : NormalizeValue(reader.GetValue(i));
                }

                rows.Add(row);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new McpException(provider.TranslateError(ex, database), ex);
        }

        return new QueryResult(rows, truncated, columnCount);
    }

    /// <summary>
    /// 在显式事务中执行写语句（INSERT/UPDATE/DELETE/DDL）。
    /// commit=false 时执行后回滚（演练模式），commit=true 时提交。
    /// </summary>
    public async Task<int> ExecuteInTransactionAsync(
        string database, string sql, bool commit, CancellationToken ct = default)
    {
        var provider = GetProvider(database);
        var connection = await OpenConnectionAsync(database, ct);
        await using var _ = connection;

        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.CommandTimeout = Math.Max(1, _options.CommandTimeoutSeconds);

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

            throw new McpException(provider.TranslateError(ex, database), ex);
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
