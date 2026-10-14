using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlServerMcp.Configuration;
using SqlServerMcp.Services;
using SqlServerMcp.Tools;

var builder = Host.CreateApplicationBuilder(args);

// 配置文件定位策略：优先使用当前工作目录下的 appsettings.json（便于直接编辑项目目录中的配置），
// 但 CWD 下可能存在与本服务无关的同名配置（.NET 项目极常见），仅当包含 SqlServer 配置节时才采用，
// 否则回退到输出目录中的副本。加载后清空默认配置源并按此顺序重建。
var configDirectory = ResolveConfigDirectory();
var configurationSources = (IConfigurationBuilder)builder.Configuration;
configurationSources.Sources.Clear();
configurationSources
    .SetBasePath(configDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();

// 强类型配置绑定
builder.Services.Configure<SqlServerOptions>(
    builder.Configuration.GetSection(SqlServerOptions.SectionName));

// 核心服务注册为单例（各驱动连接池内部自行管理物理连接）
builder.Services.AddSingleton<DatabaseProviderFactory>();
builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddSingleton<SqlGuardService>();

// 审计日志：日志目录挂在配置目录下（logs/audit-YYYYMMDD.log）
builder.Services.AddSingleton(serviceProvider => new AuditLogger(
    configDirectory,
    serviceProvider.GetRequiredService<ILogger<AuditLogger>>()));

// MCP Server：stdio 本地传输，注册工具类型
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ReadTools>()
    .WithTools<WriteTools>();

// 日志全部输出到 stderr：stdout 是 MCP JSON-RPC 协议通道，不能被日志污染
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

await builder.Build().RunAsync();

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
