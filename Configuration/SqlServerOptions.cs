namespace SqlServerMcp.Configuration;

/// <summary>
/// SqlServer MCP 配置根节点，对应 appsettings.json 的 "SqlServer" 节。
/// 支持环境变量覆盖，分层分隔符为双下划线，例如：
/// SqlServer__Databases__ExampleDb__ConnectionString=...
/// </summary>
public class SqlServerOptions
{
    public const string SectionName = "SqlServer";

    /// <summary>
    /// 已授权数据库字典：键为数据库别名（MCP 工具 database 参数使用），值为连接信息。
    /// 工具只能访问此处登记的数据库，越权即报错。
    /// </summary>
    public Dictionary<string, DatabaseEntry> Databases { get; set; } = new();

    /// <summary>SQL 命令超时时间（秒）。</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>是否允许写操作（execute_non_query / execute_ddl）。设为 false 时写工具直接拒绝执行。</summary>
    public bool AllowWrite { get; set; } = true;

    /// <summary>
    /// 审计日志保留天数（按 UTC 日期切分的 audit-YYYYMMDD.log）。0 表示不清理。
    /// 启动时清理早于该天数的已完成日志文件；当日文件与目录内其他文件永不删除。
    /// </summary>
    public int AuditRetentionDays { get; set; } = 30;

    /// <summary>
    /// 启动时是否对所有授权库做连通性预检（默认关闭）。开启后会增加启动耗时并对每个库建立一次连接，
    /// 失败仅记录警告、不阻断启动。
    /// </summary>
    public bool StartupConnectivityCheck { get; set; } = false;

    /// <summary>
    /// 危险关键词黑名单：语句中（忽略注释与字符串字面量后）出现即拒绝执行，防止误操作整个实例。
    /// 覆盖四类：实例级操作、权限管理语句（CREATE/ALTER/DROP LOGIN、服务器角色、授权等）、
    /// 只读路径上的数据外发与文件读取函数（OPENROWSET、dblink、pg_read_file、LOAD_FILE、INTO OUTFILE 等）。
    /// </summary>
    public List<string> DangerousKeywords { get; set; } = new()
    {
        // 实例级操作
        "DROP DATABASE",
        "CREATE DATABASE",
        "ALTER DATABASE",
        "RESTORE DATABASE",
        "BACKUP DATABASE",
        "ATTACH_DB",
        "xp_cmdshell",
        "sp_configure",
        "SHUTDOWN",
        "KILL",
        // 权限管理 / 提权
        "CREATE LOGIN",
        "ALTER LOGIN",
        "DROP LOGIN",
        "CREATE SERVER ROLE",
        "ALTER SERVER ROLE",
        "DROP SERVER ROLE",
        "ALTER AUTHORIZATION",
        "GRANT",
        "REVOKE",
        "DENY",
        // 只读路径数据外发 / 文件读取（SELECT 亦可触发）
        "OPENROWSET",
        "OPENDATASOURCE",
        "OPENQUERY",
        "xp_dirtree",
        "sp_OACreate",
        "dblink",
        "pg_read_file",
        "pg_read_binary_file",
        "pg_ls_dir",
        "lo_import",
        "lo_export",
        "LOAD_FILE",
        "INTO OUTFILE",
        "INTO DUMPFILE"
    };
}

/// <summary>单个授权数据库的连接信息。</summary>
public class DatabaseEntry
{
    /// <summary>完整连接字符串（SQL Server 身份验证或 Windows 集成验证均可）。</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>数据库说明，展示给模型帮助其选择合适的库。</summary>
    public string? Description { get; set; }

    /// <summary>
    /// 数据库类型：sqlserver / mysql / postgresql / sqlite。
    /// 缺省为 sqlserver，保证未填写该字段的旧配置继续按 SQL Server 处理。
    /// </summary>
    public string Type { get; set; } = "sqlserver";
}
