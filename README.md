# SqlServerMcp — 本地多数据库 MCP Server

基于 .NET 8 + [ModelContextProtocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) 的本地 MCP 服务器，让 AI（CodeBuddy 等 MCP 客户端）能够连接并操作本机**已授权**的多个数据库（SQL Server / MySQL / PostgreSQL / SQLite）：结构探查、只读查询，以及带二次确认机制的完整 CRUD/DDL 写操作（四类数据库均支持）。

## 功能特性

- **多数据库类型**：SQL Server、MySQL、PostgreSQL、SQLite 四种类型统一接入（Provider 抽象，目录查询/错误提示/写能力按方言适配）；写操作全部走二次确认，可通过 `AllowWrite` 整体关闭。
- **多数据库白名单**：在 `appsettings.json` 中登记多个数据库连接，工具通过 `database` 参数切换；未登记的库直接拒绝并提示可用列表，从根上防止越权访问。
- **SQL Server 两种验证方式**：身份验证（账号密码）与 Windows 集成验证均可配置，连接字符串完全由配置文件提供。
- **只读工具集**：连通性检查、列库、列表、表结构（列/主键/外键）、索引、视图、存储过程、SELECT 查询（强制行数上限）、结果导出（CSV/Markdown/JSON）。
- **写操作二次确认**：`confirm=false`（默认）进入**演练模式**——语句在事务中执行后回滚，返回预估影响行数（MySQL 的 DDL 会隐式提交、无法回滚，故演练时跳过执行并提示）；确认无误后 `confirm=true` 才真正提交。
- **安全防护**：危险关键词黑名单（`DROP DATABASE`、`CREATE LOGIN`、`GRANT` 等实例级与提权语句，以及各数据库方言的例程/触发器/程序集创建语句，可配置）、`execute_query` 只读加固（穿透 CTE 识别主语句类型，拦截 `WITH ... INSERT/UPDATE/DELETE` 与 `SELECT ... INTO`）、守卫下沉到服务层（查询/写入的唯一入口内置校验，新工具无法绕过）、写操作单语句限制、无 WHERE 的 UPDATE/DELETE 强制显式确认、`CommandTimeout` 限制、连接字符串不回显。
- **写操作审计日志**：写操作提交/演练、守卫拒绝、执行失败均落盘 `logs/audit-YYYYMMDD.log`（JSON 行、UTC 时间戳；SQL 仅存哈希与前 200 字符预览）。
- **stdio 本地运行**：日志全部走 stderr，不污染 JSON-RPC 协议通道。

## 环境要求

- .NET 8 SDK（或更高版本）
- 可访问的数据库实例（按需）：SQL Server（Windows 下可为 LocalDB / SQLEXPRESS / 默认实例；macOS / Linux 无本地实例，需连远程或 Docker）、MySQL、PostgreSQL、SQLite（文件型无需服务）

## 快速开始

```bash
# 1. 还原并构建
dotnet build

# 2. 编辑 appsettings.json，填写你的数据库连接（见下文配置说明）

# 3. 运行（--no-build 避免构建输出干扰 stdio 通道）
dotnet run --no-build
```

生产/稳定使用建议发布为独立可执行文件（避免 `dotnet run` 首次构建输出干扰 stdio）：

```bash
dotnet publish -c Release -o publish
# Windows:      之后直接运行 publish\SqlServerMcp.exe
# macOS / Linux: chmod +x publish/SqlServerMcp && 运行 publish/SqlServerMcp
```

> **重要**：`publish/` 是发布时的**快照**——修改代码或 `appsettings.json` 后必须重新执行 `dotnet publish`，publish 目录与其中包含的配置副本才会更新（CodeBuddy 若配置为 `dotnet run` 则重启时自动编译，无需手动 publish）。

## 配置说明（appsettings.json）

```json
{
  "SqlServer": {
    "CommandTimeoutSeconds": 30,
    "MaxRows": 500,
    "AllowWrite": true,
    "DangerousKeywords": ["DROP DATABASE", "CREATE DATABASE", "..."],
    "Databases": {
      "MyDb": {
        "ConnectionString": "Server=localhost;Database=MyDb;User Id=sa;Password=your_password;TrustServerCertificate=True;",
        "Description": "业务主库（SQL Server 身份验证）"
      },
      "MyDbWinAuth": {
        "ConnectionString": "Server=localhost;Database=MyDbWinAuth;Integrated Security=true;TrustServerCertificate=True;",
        "Description": "报表库（Windows 集成验证）"
      }
    }
  }
}
```

| 配置项 | 说明 |
| --- | --- |
| `Databases` | 授权数据库字典。**键即 `database` 参数使用的别名**，值为连接信息（含 `Type`）与说明 |
| `Type`（条目内） | 数据库类型：`sqlserver` / `mysql` / `postgresql` / `sqlite`。**缺省为 `sqlserver`**，未填写该字段的旧配置继续按 SQL Server 处理 |
| `CommandTimeoutSeconds` | SQL 命令超时（秒），默认 30 |
| `MaxRows` | `execute_query` 默认最大返回行数，默认 500 |
| `AllowWrite` | 写操作总开关。设为 `false` 时 `execute_non_query`/`execute_ddl` 直接拒绝 |
| `DangerousKeywords` | 危险关键词黑名单（忽略注释与字符串字面量后匹配），命中即拒绝。覆盖实例级操作、权限提权语句，以及只读路径上的数据外发/文件读取函数（`OPENROWSET`、`dblink`、`pg_read_file`、`LOAD_FILE`、`INTO OUTFILE` 等） |

**各数据库类型连接串示例**：

```json
"Databases": {
  "MySqlDb": {
    "Type": "mysql",
    "ConnectionString": "Server=localhost;Port=3306;Database=库名;User=root;Password=密码;",
    "Description": "MySQL 业务库"
  },
  "PgDb": {
    "Type": "postgresql",
    "ConnectionString": "Host=localhost;Port=5432;Database=库名;Username=postgres;Password=密码;",
    "Description": "PostgreSQL 业务库"
  },
  "SqliteDb": {
    "Type": "sqlite",
    "ConnectionString": "Data Source=D:\\data\\app.db;",
    "Description": "SQLite 本地库（只读，Data Source 为 .db 文件路径）"
  }
}
```

**连接字符串要点**：
- SQL Server 身份验证：`Server=localhost;Database=库名;User Id=sa;Password=密码;TrustServerCertificate=True;`
- Windows 集成验证：`Server=localhost;Database=库名;Integrated Security=true;TrustServerCertificate=True;`
- 命名实例：`Server=localhost\SQLEXPRESS;...`
- 自签证书建议加 `TrustServerCertificate=True`，否则会报证书链错误。

**用环境变量覆盖连接字符串**（避免把密码写进文件，分层分隔符为双下划线）：

```powershell
$env:SqlServer__Databases__MyDb__ConnectionString = "Server=...;Password=..."
```

**写能力说明**：四类数据库均支持写操作，全部走 confirm 二次确认流程（见上方「各方言写操作差异」表了解 MySQL DDL 的特殊处理）。如需整体关闭写能力，把 `AllowWrite` 设为 `false` 即可。

**审计日志**：写操作的提交/演练、守卫拒绝（黑名单/只读/单语句等拦截）、写执行失败都会以 JSON 行追加到 `<配置目录>/logs/audit-YYYYMMDD.log`。每条记录包含 UTC 时间戳、库别名、Provider、工具名、模式（commit/dry-run）、影响行数，以及 SQL 的 **SHA256 哈希与前 200 字符预览**（不落完整语句，避免敏感字面量全量落盘）。日志写入失败不会中断主流程（降级为 stderr 警告）。

```json
{"ts":"2026-10-14T08:45:18.1234567+00:00","event":"write","database":"PLC_DB","provider":"mysql","tool":"execute_ddl","mode":"commit","affected_rows":0,"sql_sha256":"…","sql_preview":"CREATE TABLE …"}
```

## 接入 CodeBuddy

打开 CodeBuddy：**Settings → MCP → Add MCP**，按下表选择对应系统的写法（MCP 客户端直接以数组传参启动进程，不经过 shell，**路径含空格无需额外转义**）。

### Windows

**方式 1：`dotnet run`（开发期，重启即自动编译最新代码）**

```json
{
  "mcpServers": {
    "sqlserver-mcp": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "d:\\项目\\MCP 数据库\\SqlServerMcp.csproj"
      ],
      "description": "本地多数据库（SQL Server/MySQL/PostgreSQL/SQLite）操作"
    }
  }
}
```

**方式 2：发布后的可执行文件（更稳定，推荐）**

```powershell
dotnet publish -c Release -o publish
```

```json
{
  "mcpServers": {
    "sqlserver-mcp": {
      "type": "stdio",
      "command": "d:\\项目\\MCP 数据库\\publish\\SqlServerMcp.exe",
      "args": [],
      "description": "本地多数据库（SQL Server/MySQL/PostgreSQL/SQLite）操作"
    }
  }
}
```

### macOS / Linux（Unix 风格路径）

路径使用正斜杠（`/`），**不需要**反斜杠转义。下面示例假设项目位于 `~/projects/MCP 数据库`，请换成你的实际路径（可用 `pwd` 查看；`~` 在配置中建议展开为绝对路径，如 `/Users/你的用户名/projects/MCP 数据库`）。

**方式 1：`dotnet run`（开发期）**

```json
{
  "mcpServers": {
    "sqlserver-mcp": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/Users/你的用户名/projects/MCP 数据库/SqlServerMcp.csproj"
      ],
      "description": "本地多数据库（SQL Server/MySQL/PostgreSQL/SQLite）操作"
    }
  }
}
```

**方式 2：发布后的可执行文件（更稳定，推荐）**

```bash
dotnet publish -c Release -o publish
chmod +x publish/SqlServerMcp        # 首次需赋予执行权限（Unix 可执行位）
```

```json
{
  "mcpServers": {
    "sqlserver-mcp": {
      "type": "stdio",
      "command": "/Users/你的用户名/projects/MCP 数据库/publish/SqlServerMcp",
      "args": [],
      "description": "本地多数据库（SQL Server/MySQL/PostgreSQL/SQLite）操作"
    }
  }
}
```

> 注意：Unix 平台发布产物是**无扩展名**的可执行文件 `publish/SqlServerMcp`（不是 `.exe`），且必须可执行（`chmod +x`）。

**macOS / Linux 补充说明**

| 事项 | 说明 |
| --- | --- |
| 安装 .NET | 需自行安装 .NET 8 SDK（[官网安装包](https://dotnet.microsoft.com/download) 或 `brew install --cask dotnet-sdk`）；`dotnet --version` 可确认 |
| 客户端找不到 `dotnet` | GUI 启动的客户端可能不继承 shell 的 PATH。把 `command` 写成绝对路径：Apple Silicon 常用 `/opt/homebrew/bin/dotnet`，Intel 包安装通常为 `/usr/local/share/dotnet/dotnet`（用 `which dotnet` 查） |
| SQL Server 目标 | macOS 上**没有本地 SQL Server**：`Type: sqlserver` 的条目请指向远程实例或 Docker 容器（`mcr.microsoft.com/mssql/server`）；MySQL / PostgreSQL / SQLite 均可原生连接（SQLite 为文件路径，如 `Data Source=/Users/你的用户名/data/app.db;`） |
| 路径中的空格与中文 | `/Users/.../MCP 数据库/...` 这类路径直接写即可，无需加引号或转义 |
| 配置文件权限 | `appsettings.json` 含明文连接串，建议 `chmod 600 appsettings.json`；审计日志目录 `logs/` 同样注意权限 |

保存后状态显示为**绿色**即接入成功，可点击 **Try to Run** 验证。之后在对话中直接让 AI「查一下 MyDb 库有哪些表」即可。

> 注意：以上路径请按实际存放位置调整——Windows 的 JSON 字符串中反斜杠需转义为 `\\`；macOS / Linux 用正斜杠且无需转义。

## 工具清单

| 工具 | 类型 | 说明 |
| --- | --- | --- |
| `list_databases` | 只读 | 列出所有已授权数据库别名、类型（sqlserver/mysql/postgresql/sqlite）及说明 |
| `list_tables` | 只读 | 列出指定库的所有表（schema、表名、近似行数；SQLite 无行统计返回 null） |
| `describe_table` | 只读 | 表结构：列（类型/长度/可空/默认值）、主键、外键 |
| `list_indexes` | 只读 | 索引列表（可按表过滤，含键列/包含列/是否唯一；SQLite 不含键列明细；MySQL 键列受 group_concat_max_len 限制） |
| `list_views` | 只读 | 视图列表（含定义前 500 字符） |
| `list_stored_procedures` | 只读 | 存储过程/函数列表（含定义前 500 字符；SQLite 无存储过程返回空列表） |
| `test_connection` | 只读 | 连通性检查：Provider 类型、服务器版本、当前库名、连接耗时（毫秒） |
| `execute_query` | 只读 | 执行单条 SELECT/WITH 查询，返回 JSON 数组，行数受上限约束 |
| `export_query_result` | 只读 | 执行只读查询并导出为 csv / markdown / json（CSV 含公式注入防护） |
| `execute_non_query` | 写 | 执行单条 INSERT/UPDATE/DELETE，confirm 二次确认（四类数据库均支持） |
| `execute_ddl` | 写 | 执行单条 DDL（CREATE/ALTER/DROP/TRUNCATE 等），confirm 二次确认（四类数据库均支持） |

**各方言写操作差异**：

| 数据库 | DDL 演练（confirm=false） | 说明 |
| --- | --- | --- |
| SQL Server | 事务内执行后回滚 | 完整演练支持 |
| PostgreSQL | 事务内执行后回滚 | PG 的 DDL 可事务化 |
| SQLite | 事务内执行后回滚 | SQLite 的 DDL 可事务化 |
| MySQL | **不执行，仅返回提示** | InnoDB 的 DDL 会隐式提交、无法回滚，故演练模式 fail-closed 跳过；确认后以 confirm=true 直接执行（autocommit） |

## 安全机制详解

1. **confirm 二次确认（写操作）**
   - `confirm=false`（默认）：事务中执行后 **ROLLBACK**，返回「预估影响行数」，不产生实际变更；
   - `confirm=true`：才 COMMIT 真正提交。
   - 模型/用户应先以默认模式演练，确认影响行数符合预期后再确认提交。

2. **高风险语句拦截**
   - 无 `WHERE` 的 UPDATE/DELETE：必须显式 `confirm=true` 才执行；
   - 黑名单关键词（`DROP DATABASE`、`CREATE/ALTER/DROP LOGIN`、`ALTER SERVER ROLE`、`GRANT/REVOKE/DENY`、`xp_cmdshell`、`SHUTDOWN` 等实例级与提权语句）：直接拒绝，可在配置中调整；
   - 写操作一次仅允许单条语句，防止批量误操作；
   - `execute_query` 只读加固：`WITH` 语句会穿透 CTE 定义识别主语句类型，`WITH cte AS (...) INSERT/DELETE/UPDATE` 按写操作拒绝；`SELECT ... INTO`（查询中建表写数据）同样拒绝。
   - 各数据库方言专属的危险语句（如 MySQL 的 `SET GLOBAL`/`FLUSH`/`CREATE USER`、PostgreSQL 的 `ALTER SYSTEM`/`CREATE ROLE`）与例程/触发器/程序集创建语句（函数体可藏匿危险关键词）在全局黑名单之外单独拦截。
   - MySQL 可执行注释 `/*! ... */`（内容会被服务器执行，导致校验与实际执行不一致）与未终止的字面量/注释一律**拒绝执行**（fail-closed）。

3. **导出与数据安全**
   - CSV 导出对以 `=`、`+`、`-`、`@` 开头的单元格前置单引号，防止 Excel/WPS 打开时把数据当公式/DDE 执行（合法负数不误伤）；
   - 审计日志只记录 SQL 哈希与前 200 字符预览，不落完整语句，避免业务数据与凭据散落磁盘。

4. **已知限制**
   - 演练模式回滚**仍会消耗 IDENTITY 值**（SQL Server 不回退自增序列）；
   - `CREATE DATABASE` 等实例级操作无法在事务中回滚，已被黑名单直接拦截（如需使用请自行评估后调整配置）；
   - 查询结果超过行数上限会被截断；`execute_query` 会在 JSON 结果后附中文截断提示，`export_query_result` 仅对 Markdown 追加提示（CSV/JSON 保持机器可读纯净）；
   - 含分号的例程体（如 `CREATE PROCEDURE ... BEGIN ... ; END`）会先被单语句规则拒绝——这类语句本就由例程创建规则整体拦截；
   - MySQL 的 `group_concat_max_len`（默认 1024 字符）可能截断 `list_indexes` 的键列拼接。

## 故障排查

| 现象 | 排查 |
| --- | --- |
| 连不上库（SQL Server 错误 53/40/10061） | SQL Server 服务是否启动；TCP/IP 协议是否启用（SQL Server 配置管理器）；防火墙 1433 端口 |
| 登录失败（SQL Server 错误 18456） | 检查用户名/密码；集成验证时确认当前 Windows 账户有库访问权限 |
| MySQL 登录失败（错误 1045）/ 数据库不存在（1049） | 检查用户名/密码与 Database 名称；账户是否已授权访问该库 |
| PostgreSQL 登录失败（28P01）/ 数据库不存在（3D000） | 检查 Username/Password 与 Database 名称 |
| 提示未授权数据库 | `database` 参数必须与 `appsettings.json` 中 `Databases` 的键一致 |
| 提示不支持的数据库类型 | 条目 `Type` 只能是 sqlserver/mysql/postgresql/sqlite（缺省 sqlserver） |
| 构建时 NuGet 还原失败 | 项目已附带 `nuget.config` 锁定 nuget.org 源；若公司内网有专用源，可在其中追加 |
| MCP 状态为红色 | 确认 `dotnet` 在 PATH 中；先用命令行 `dotnet run` 手动启动看 stderr 输出 |

## 项目结构

```
├── Program.cs                   # 入口：Generic Host + stdio 传输 + DI + stderr 日志
├── appsettings.json             # 配置：授权数据库、超时、行数上限、黑名单
├── Configuration/
│   └── SqlServerOptions.cs      # 强类型配置模型
├── Services/
│   ├── IDatabaseProvider.cs       # Provider 接口（连接/目录查询/转义/能力标志/错误翻译约定）
│   ├── DatabaseProviderFactory.cs # 按 Type 创建并缓存 Provider
│   ├── DatabaseService.cs         # 白名单解析、Provider 路由、通用执行、只读/写入守卫入口
│   ├── SqlGuardService.cs         # 语句分类、单语句校验、黑名单、只读守卫、无 WHERE 识别
│   ├── TableNameParser.cs         # 引号标识符感知的表名解析（四 Provider 共用）
│   ├── ExportFormatter.cs         # CSV / Markdown / JSON 导出（含公式注入防护）
│   ├── AuditLogger.cs             # 写操作与守卫拒绝的 JSON 行审计日志
│   └── Providers/
│       ├── SqlServerProvider.cs   # SQL Server（sys.* 目录）
│       ├── MySqlProvider.cs       # MySQL（information_schema，DDL 不可事务化）
│       ├── PostgreSqlProvider.cs  # PostgreSQL（pg_catalog + information_schema）
│       └── SqliteProvider.cs      # SQLite（sqlite_master + PRAGMA）
└── Tools/
    ├── ReadTools.cs             # 9 个只读工具（含 test_connection / export_query_result）
    └── WriteTools.cs            # 2 个写工具（confirm 二次确认）
tests/
└── SqlServerMcp.Tests/          # xUnit 测试：守卫 / 方言 / 导出 / 审计 / SQLite 写路径集成（175 项）
logs/                            # 运行时生成的审计日志（已 gitignore）
```

## 安全提示

- 本项目会授予 AI 对授权数据库的完整写操作能力（四类数据库均支持），请谨慎配置 `Databases`（建议只登记必要的库）并妥善保管包含连接字符串的 `appsettings.json`（已被 `.gitignore` 排除，请勿提交到公开仓库；敏感场景请用环境变量 `SqlServer__Databases__<库名>__ConnectionString` 传密码）。`logs/` 目录已纳入 `.gitignore`（含 SQL 预览与库名）。
- 注意 `bin/` 与 `publish/` 目录中的 `appsettings.json` 是含凭据的副本（已被 `.gitignore` 覆盖），分享这两个目录前请确认其中没有真实密码。
- 建议为 MCP 创建专用低权 SQL 登录（按库授权），避免直接使用 `sa`。
- 如只希望 AI 做查询分析，可将 `AllowWrite` 设为 `false`。
