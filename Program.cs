using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using SqlServerMcp.Configuration;
using SqlServerMcp.Services;
using SqlServerMcp.Tools;
using System.Text.Json;

// 配置目录解析先完成（stdio / http 两条路径共用）
var configDirectory = ResolveConfigDirectory();
var transport = ResolveTransport(configDirectory);

// http 模式：走 ASP.NET Core（Streamable HTTP 传输），支持远程/多客户端；鉴权由 ApiKey 中间件负责
if (string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
{
    await RunHttpAsync(args, configDirectory);
    return;
}

// ===== stdio（默认，行为与原实现一致）=====
var builder = Host.CreateApplicationBuilder(args);
ConfigureConfigSources(builder.Configuration, configDirectory);
ConfigureServices(builder.Services, configDirectory, builder.Configuration, "stdio");

// 日志全部输出到 stderr：stdout 是 MCP JSON-RPC 协议通道，不能被日志污染
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

var host = builder.Build();

// 自检在启动传输之前执行：stdio 服务若启动即失败，客户端侧拿不到任何诊断信息，
// 因此这里只输出警告、绝不退出（详见 StartupDiagnostics 的设计说明）。
await host.Services.GetRequiredService<StartupDiagnostics>().RunAsync();

await host.RunAsync();

static async Task RunHttpAsync(string[] args, string configDirectory)
{
    var builder = WebApplication.CreateBuilder(args);
    ConfigureConfigSources(builder.Configuration, configDirectory);
    ConfigureServices(builder.Services, configDirectory, builder.Configuration, "http");

    var options = builder.Configuration.GetSection(SqlServerOptions.SectionName).Get<SqlServerOptions>()!;
    // Kestrel 监听地址通过 urls 配置键注入（ConfigureWebHostBuilder 无 UseUrls 扩展），0.0.0.0 允许容器/远程访问
    builder.Configuration["urls"] = $"http://0.0.0.0:{options.HttpPort}";

    // http 模式日志沿用默认控制台输出（无 stdio 协议，stdout 不被协议占用）；不重复 AddConsole 避免日志翻倍
    var app = builder.Build();

    // 鉴权中间件：仅保护 /mcp 端点（其余路径如健康检查保持可达，便于探活）；未配置 ApiKey 一律 401（fail-closed）
    var apiKey = options.ApiKey;
    app.Use(async (context, next) =>
    {
        if (HttpApiKeyAuth.IsMcpPath(context.Request.Path) &&
            !HttpApiKeyAuth.IsAuthorized(context.Request, apiKey))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        await next();
    });

    // 运维看板：静态资源（wwwroot/dashboard）与只读 JSON API。
    // 顺序要求：静态文件中间件必须在鉴权中间件之后、MapMcp 之前。
    // 注意：静态文件中间件对**所有**路径生效，因此 wwwroot 下不得出现名为 "mcp" 的文件或目录，
    // 否则会短路 MCP 端点（wwwroot 内只放 dashboard/ 子目录即可规避）。
    app.UseDefaultFiles();
    app.UseStaticFiles();

    await app.Services.GetRequiredService<StartupDiagnostics>().RunAsync();
    app.MapMcp("/mcp");
    app.MapGet("/", () => Results.Redirect($"{DashboardEndpoints.BasePath}/"));
    DashboardEndpoints.Map(app);
    await app.RunAsync();
}

/// <summary>
/// 配置源重建（stdio / http 共用）：清空默认源，仅保留配置目录下的 appsettings.json（可选）与环境变量覆盖。
/// optional:true 保证配置文件缺失时不以 FileNotFoundException 退出，由启动自检给出中文提示。
/// </summary>
static void ConfigureConfigSources(IConfigurationManager config, string configDirectory)
{
    config.Sources.Clear();
    config.SetBasePath(configDirectory)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
        .AddEnvironmentVariables();
}

/// <summary>注册核心服务与 MCP Server（含工具与传输方式选择）。</summary>
static void ConfigureServices(IServiceCollection services, string configDirectory, IConfiguration configuration, string transport)
{
    services.Configure<SqlServerOptions>(configuration.GetSection(SqlServerOptions.SectionName));

    services.AddSingleton<DatabaseProviderFactory>();
    services.AddSingleton<DatabaseService>();
    services.AddSingleton<SqlGuardService>();

    services.AddSingleton(serviceProvider => new AuditLogger(
        configDirectory,
        serviceProvider.GetRequiredService<ILogger<AuditLogger>>()));

    services.AddSingleton(serviceProvider => new StartupDiagnostics(
        serviceProvider.GetRequiredService<IOptions<SqlServerOptions>>(),
        serviceProvider.GetRequiredService<DatabaseProviderFactory>(),
        serviceProvider.GetRequiredService<AuditLogger>(),
        configDirectory,
        serviceProvider.GetRequiredService<ILogger<StartupDiagnostics>>()));

    if (string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
    {
        // 看板连通性探测缓存：仅 http 模式有 Web 宿主，stdio 模式不注册也不实例化。
        // 探测委托直接复用 GetAllServerInfoAsync（并行、单库 5 秒超时、单库失败隔离）。
        services.AddSingleton(serviceProvider =>
        {
            var databaseService = serviceProvider.GetRequiredService<DatabaseService>();
            return new DashboardProbeCache(
                async (timeoutSeconds, ct) =>
                {
                    var results = await databaseService.GetAllServerInfoAsync(timeoutSeconds, ct);
                    return results
                        .Select(item => new DatabaseProbeResult(item.Database, item.Info, item.Error))
                        .ToList();
                },
                serviceProvider.GetRequiredService<ILogger<DashboardProbeCache>>());
        });
    }

    var mcp = services
        .AddMcpServer()
        .WithTools<ReadTools>()
        .WithTools<WriteTools>()
        .WithTools<AuditTools>();

    if (string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
    {
        // Streamable HTTP 传输：会话/鉴权通过 HttpServerTransportOptions 配置；本地白名单不足，靠 ApiKey 中间件兜底
        mcp.WithHttpTransport(_ => { });
    }
    else
    {
        mcp.WithStdioServerTransport();
    }
}

static string ResolveConfigDirectory()
{
    var currentDirectory = Directory.GetCurrentDirectory();
    if (IsValidConfigDirectory(currentDirectory))
    {
        return currentDirectory;
    }

    return AppContext.BaseDirectory;
}

/// <summary>校验目录中的 appsettings.json 是否包含本服务的 SqlServer 配置节，避免误加载无关配置。</summary>
static bool IsValidConfigDirectory(string directory)
{
    var path = Path.Combine(directory, "appsettings.json");
    if (!File.Exists(path))
    {
        return false;
    }

    try
    {
        return File.ReadAllText(path).Contains("\"SqlServer\"", StringComparison.OrdinalIgnoreCase);
    }
    catch
    {
        return false;
    }
}

/// <summary>解析传输方式（白名单校验）：优先环境变量 SqlServer__Transport，其次 appsettings.json 的 SqlServer:Transport；无法识别时告警并回退 stdio。</summary>
static string ResolveTransport(string configDirectory)
{
    var raw = ReadRawTransport(configDirectory);
    var normalized = (raw ?? string.Empty).Trim().ToLowerInvariant();

    if (normalized is "stdio" or "http")
    {
        return normalized;
    }

    // M2：无法识别的值不能静默回退——输出 stderr 告警，避免部署者误以为起了 HTTP
    if (!string.IsNullOrEmpty(normalized))
    {
        Console.Error.WriteLine($"[警告] 配置 SqlServer:Transport=\"{raw}\" 无法识别，已回退为 stdio（仅支持 stdio / http）。");
    }

    return "stdio";
}

/// <summary>读取未校验的传输方式原始值：优先环境变量 SqlServer__Transport，其次 appsettings.json 的 SqlServer:Transport；缺省返回 null。</summary>
static string? ReadRawTransport(string configDirectory)
{
    var env = Environment.GetEnvironmentVariable("SqlServer__Transport");
    if (!string.IsNullOrWhiteSpace(env))
    {
        return env;
    }

    var path = Path.Combine(configDirectory, "appsettings.json");
    if (!File.Exists(path))
    {
        return null;
    }

    try
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.TryGetProperty("SqlServer", out var sqlServer) &&
            sqlServer.TryGetProperty("Transport", out var transportValue) &&
            transportValue.ValueKind == JsonValueKind.String)
        {
            return transportValue.GetString();
        }
    }
    catch
    {
        // 解析失败时交由上层走默认值，不阻断启动
    }

    return null;
}
