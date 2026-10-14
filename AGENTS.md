# AGENTS.md

.NET 8 C# 项目：本地 stdio MCP Server（`ModelContextProtocol` SDK），让 AI 客户端操作已授权的多数据库。四类数据库（SQL Server / MySQL / PostgreSQL / SQLite）均支持读写，写操作带 confirm 二次确认演练；方言差异（如 MySQL DDL 隐式提交不可演练）由各 Provider 的能力标志控制。含写操作审计日志。

**语言约定**：README、代码注释、工具 Description 全部为中文，新增文档/注释保持一致。

## 命令

```powershell
dotnet build                              # 构建（无 .sln，根目录直接构建主项目）
dotnet build -o bin-test                  # 构建到独立目录；被运行中实例锁住 bin/ 时用这个
dotnet run --no-build                     # 运行；必须加 --no-build，避免构建输出污染 stdio 通道
dotnet test tests/SqlServerMcp.Tests      # 跑全部无依赖测试（296 项；集成测试被默认过滤器排除，见下）
dotnet publish -c Release -o publish      # 发布快照；publish/ 不是热更新，改代码/配置后必须重新 publish
```

单测（测试方法名是中文，`FullyQualifiedName~` 可直接匹配中文片段）：

```powershell
dotnet test tests/SqlServerMcp.Tests -o bin-verify --filter "FullyQualifiedName~SqlGuardServiceTests"  # 单个测试类
dotnet test tests/SqlServerMcp.Tests -o bin-verify --filter "FullyQualifiedName~例程"                  # 跨类关键字
dotnet test tests/SqlServerMcp.Tests -o bin-verify --filter "FullyQualifiedName~<中文方法名>"          # 单个测试方法
```

集成测试（真实 MySQL / PostgreSQL，连接串只经环境变量传入）：

```powershell
docker compose -f docker-compose.test.yml up -d        # 起 MySQL 8（3307）与 PostgreSQL 16（5433）
$env:SQLSERVERMCP_IT_MYSQL = "Server=127.0.0.1;Port=3307;Database=mcp_it;User=root;Password=it_root_pw;AllowPublicKeyRetrieval=True;"
$env:SQLSERVERMCP_IT_POSTGRESQL = "Host=127.0.0.1;Port=5433;Database=mcp_it;Username=postgres;Password=it_pg_pw;"
dotnet test tests/SqlServerMcp.Tests --filter "Category=Integration"   # 显式 filter 覆盖测试项目的默认排除值
```

验证顺序：`dotnet build` → `dotnet test`。项目无 lint/format/typecheck 配置。

## 结构

无解决方案文件，两个 csproj 并存；主项目用 `<Compile Remove="tests\**\*.cs" />` 排除测试代码，根目录其余 `.cs` 都会编入主程序集。

```
Program.cs                    # 入口：Generic Host + stdio 传输 + DI + WithTools 注册 + 启动自检调用；所有日志走 stderr
appsettings.example.json      # 可提交的配置模板（无真实凭据）；CI 与发布产物用它生成 appsettings.json
Configuration/SqlServerOptions.cs  # 强类型配置，绑定 "SqlServer" 节
Services/
  DatabaseService.cs          # 白名单解析、Provider 路由、通用执行；查询/写入的唯一受认可入口
  SqlGuardService.cs          # SQL 安全守卫（见下「安全不变量」）
  DatabaseProviderFactory.cs  # 按 Type 字符串创建 Provider（大小写不敏感，缺省 sqlserver）
  TableNameParser.cs          # 引号标识符感知的表名解析（四 Provider 共用）
  ExportFormatter.cs          # CSV / Markdown / JSON 导出纯函数（含公式注入防护）
  AuditLogger.cs              # 审计日志：写入 + 查询（QueryEntries）+ 保留清理（PruneOldFiles）
  StartupDiagnostics.cs       # 启动自检：配置校验（纯函数）+ 可选连通性预检，只告警不阻断
  Providers/*.cs              # 四种数据库方言实现（唯一生成方言 SQL 的地方）
Tools/
  ReadTools.cs                # 9 个只读工具（7 探查/查询 + test_connection + export_query_result）
  WriteTools.cs               # 2 个写工具：execute_non_query / execute_ddl
  AuditTools.cs               # query_audit_log（日志目录服务端固定，不接受路径参数）
Dockerfile / .dockerignore    # 容器化：stdio 运行（docker run -i），配置与日志靠挂载
docker-compose.test.yml       # 本地集成测试依赖（MySQL 8:3307 + PostgreSQL 16:5433）
.github/workflows/ci.yml      # CI：构建 + 单元测试 + service 容器集成测试
.github/workflows/release.yml # 发布：tag v* 触发四平台产物，打包前剔除真实配置
tests/SqlServerMcp.Tests/     # xUnit 表驱动测试（守卫 / 写守卫 / 例程拦截 / 方言 / 表名 / 导出 / 审计 / 自检 /
                              #   示例配置契约 / SQLite 写路径）+ IntegrationDatabase 夹具与 MySQL/PG 集成测试
```

**分层契约**：Tools 只负责参数校验、审计埋点与中文文案；**所有方言 SQL（目录查询、版本探测、标识符转义/切割）由 Provider 生成**，`DatabaseService` 从不拼方言 SQL。新增方言只需加 Provider，不动服务层与工具层（`ProviderTests` 断言目录查询的列别名契约）。

**数据流与三条执行路径**（用户 SQL 只能走前两条，守卫在服务层内置）：

1. 只读：`AssertReadOnlyQuery` → `ExecuteReadOnlyQueryAsync` / `ExecuteQueryAsync`；
2. 事务写：`ValidateWriteStatement` → `ExecuteInTransactionAsync`（`allowedTypes` 必填白名单，`confirm` 决定提交还是回滚）；
3. 例外：`SupportsTransactionalDdl=false`（MySQL）的 DDL 走 autocommit 直执行，该判定发生在**建立连接之前**。

目录/版本工具直接调用 `ExecuteQueryAsync` 执行**自身生成的** SQL；用户提供的 SQL 一律走上面的守卫入口。

**结果归一化与装配**：`QueryResult` 统一归一（`NormalizeValue` 把 DateTime / byte[] / NaN·Infinity 转字符串，`Truncated` 标记截断，列名归一），工具层按统一别名取值。工具是 `[McpServerToolType]` 的静态方法、依赖由 SDK 注入——**新增工具必须同步在 `Program.cs` 的 `WithTools<>` 注册**。`IOptions<SqlServerOptions>` 是**启动快照**，配置热更新不影响本服务（黑名单合并结果因此可安全缓存）。

## 关键机制（易踩坑）

- **stdout 是 JSON-RPC 协议通道**：任何 Console 输出/新日志都必须进 stderr（`Program.cs` 已设 `LogToStandardErrorThreshold = LogLevel.Trace`）。`dotnet run` 不加 `--no-build` 会因构建输出破坏协议。
- **运行中的 MCP 实例会锁定构建产物**：CodeBuddy 客户端以 `dotnet run` 启动 → 锁定 `bin\Debug\net8.0\SqlServerMcp.exe`；OpenCode 指向 `publish\SqlServerMcp.exe` → 锁定 `publish\`。此时 `dotnet build` / `dotnet test` 会以 `MSB3026/MSB3027: Could not copy apphost.exe … locked by SqlServerMcp (PID)` 失败。处理：加 `-o <独立目录>`（如 `bin-test`），或先在客户端停止/重启该 MCP 服务。
- **配置加载顺序**（`Program.cs` `ResolveConfigDirectory`）：优先 CWD 的 `appsettings.json`，但仅当其含 `"SqlServer"` 节才采用，否则回退到 `AppContext.BaseDirectory` 的副本。环境变量覆盖用双下划线：`SqlServer__Databases__<别名>__ConnectionString`。配置源在 `Sources.Clear()` 后只重建为 JsonFile + 环境变量——**命令行配置源是被刻意移除的**（曾导致同名配置源歧义），不要"顺手加回"。启动日志首行会打印实际生效的配置目录，改配置不生效时先看它。
- **`appsettings.json` 已被 `.gitignore` 排除**（含真实连接密码），`bin/`、`publish/` 中的副本同样含密码。**不要**提交它，也不要用模板覆盖用户现有配置。其中 `OnlineQuotationSystem` 的库名连接串里含零宽字符 U+200C，是刻意保留的（真实库名如此），编辑时勿"清理"。
- **`nuget.config` 必须保留**：`<clear />` 锁定 nuget.org，因为本机全局 NuGet 配置存在不可达源会导致还原失败。
- **新增数据库类型**：实现 `IDatabaseProvider`（`SupportsWrite` / `SupportsTransactionalDdl` / `ExtraDangerousKeywords` / `RoutineCreationPattern` / `BuildServerVersionSql` 五项能力 + 目录查询）→ 在 `DatabaseProviderFactory` 注册 → 更新 README 与 `ProviderTests`。
- **`MaxRows` 已移除**：曾是无代码读取的死配置，已从 `SqlServerOptions` 删除（三期的 `ExampleConfigContractTests` 会断言示例模板里不再出现它）。行数由各工具的 `maxRows` 参数控制（默认 100，Clamp 到 1–5000）。
- **测试默认排除集成测试**：测试 csproj 里设了 `<VSTestTestCaseFilter>Category!=Integration</VSTestTestCaseFilter>`，因此裸 `dotnet test` 永远零依赖可跑；CI 的集成 job 用显式 `--filter "Category=Integration"` **覆盖**该默认值（已验证覆盖生效）。改这个属性时务必确认两个方向都对。
- **启动自检只告警不阻断**：`StartupDiagnostics.RunAsync` 在 `host.RunAsync()` 之前执行，配置问题只输出 stderr 警告。**不要**把它改成"有问题就退出"——stdio 服务启动即退出会让客户端侧完全拿不到诊断信息。配置文件的 `AddJsonFile` 是 `optional: true`，缺失时同样走警告路径而不是抛异常。
- **`out/` 已 gitignore**：RID 发布与 release 打包的临时目录，其中含 `appsettings.json` 副本（可能带真实凭据），禁止提交。
- **`dotnet run` 与 `--no-build`**：本地手工测试时建议加 `--no-build`（避免构建输出污染 stdio）；作为 MCP 客户端配置时**不要**加（否则改代码后重启不会重编译）。
- **`publish/` 是快照**：OpenCode 侧配置指向 `publish\SqlServerMcp.exe`，改代码或配置后必须重新 `dotnet publish -c Release -o publish` 才生效（CodeBuddy 侧是 `dotnet run`，重启自动编译）。

## 安全不变量（改动 SqlGuardService / WriteTools / Provider 前必读）

这些是项目的核心契约，破坏任何一条都算重大回归。回归防线分散在多个测试文件：`SqlGuardServiceTests`（守卫匹配）、`RoutineCreationPatternTests`（例程变体拦截 CR-1）、`ServiceWriteGuardTests` + `SqliteWriteIntegrationTests`（守卫下沉与真实事务路径）、`ExportFormatterTests`（导出安全）、`AuditLoggerTests`（审计事件）。

- **写操作两阶段**：`confirm=false`（默认）= 事务内执行后 ROLLBACK 返回预估影响行数（演练）；`confirm=true` 才 COMMIT。
- **守卫基于剥离注释与字符串字面量后的 SQL** 匹配，新增检查沿用同一前提，避免误伤字面量。剥离器对 `/*!`（MySQL 可执行注释）与未终止字面量**一律 fail-closed 拒绝**——守卫看到的 SQL 必须与实际执行的一致。
- **`WITH`（CTE）穿透**：按括号深度跳过 CTE 定义，识别其后的主语句类型，防止 `WITH cte AS (...) INSERT/UPDATE/DELETE` 绕过写确认。
- **`SELECT ... INTO` 按写操作拦截**（查询路径建表写数据）。
- 单语句限制、危险关键词黑名单 = 全局（配置可调，含 `DROP DATABASE`、`GRANT`、`xp_cmdshell`、跨方言数据外发函数）+ 各 Provider 的 `ExtraDangerousKeywords`（账号/角色/系统级与例程类创建语句），二者经 `DatabaseService.GetEffectiveDangerousKeywords` 合并（按 Provider 缓存）；无 WHERE 的 UPDATE/DELETE 需显式确认。
- **守卫下沉是结构性防线**：`DatabaseService.AssertReadOnlyQuery`/`ExecuteReadOnlyQueryAsync`（只读）与 `ValidateWriteStatement`/`ExecuteInTransactionAsync`（写入，`allowedTypes` 白名单**必填**，不传编译不过）内部各自完成守卫校验；`ExecuteQueryAsync` 与 `OpenConnectionAsync` 均为 `internal`（后者是"裸连接"逃逸口），仅程序集内部与测试（`InternalsVisibleTo`）可用——生产路径的目录/版本工具直接调用 `ExecuteQueryAsync` 执行**自身生成的** SQL，用户 SQL 一律走上面的守卫入口。
- **例程/触发器/程序集创建语句整体拒绝（CR-1）**：`IDatabaseProvider.RoutineCreationPattern` 用方言正则匹配（覆盖 `CREATE OR ALTER`/`CREATE OR REPLACE`/`ALTER`/`TEMP` 等变体——固定短语会被修饰词绕过），原因如下：语句体可藏危险词且可提权。**新增方言时若用固定短语黑名单，必须同时提供该正则。**
- **方言 fail-closed**：`SupportsTransactionalDdl=false` 的库（MySQL）DDL 在演练模式**不执行**并明确提示，且该判定发生在建立连接之前；禁止对其开事务执行 DDL（隐式提交会导致"已提交却报错"）。
- **审计日志**：写操作提交/演练、守卫拒绝（含服务层兜底拒绝）、写执行失败与建连失败均落 `<配置目录>/logs/audit-YYYYMMDD.log`（JSON 行、UTC 时间戳、SQL 仅存 SHA256 + 前 200 字符预览）；写日志失败不得中断主流程。
- **导出安全**：CSV 单元格以 `=` `+` `-` `@` 开头，或以 Unicode 空白/零宽字符（NBSP、U+3000、BOM、零宽空格等）前置时，一律前置 `'`，防 Excel/WPS 公式注入；合法负数不误伤。
- **审计查询工具无路径参数**：`query_audit_log` 的日志目录由服务端固定为 `<配置目录>/logs`，日期参数只解析 `yyyy-MM-dd` 后拼装固定文件名、条数 Clamp 到 1–200。**不要**为它增加目录/文件路径参数——那会把日志查询变成任意文件读取入口。
- **发布产物不得含真实凭据**：`release.yml` 打包前删除仓库中的 `appsettings.json`、改用 `appsettings.example.json` 生成，并有一道守护检查（出现内网地址或 8 位以上数字串即失败）。改动发布流程时保留这两步；`ExampleConfigContractTests` 是它的本地回归防线。
- 各 Provider 的目录查询 SQL 输出**列别名已归一化**（`approx_rows`、`column_name`、`pk_name`、`fk_name`、`key_columns`、`definition_head` 等），`ProviderTests` 断言此契约——工具层按这些别名取值。

## 测试

- 业务逻辑测试无需任何数据库连接（含方言相关的 fail-closed 分支——它们都在建连之前）；仅 `SqliteWriteIntegrationTests` 使用临时 SQLite 文件验证真实事务路径。
- 风格为 xUnit `[Theory]` + `[InlineData]` 表驱动，中文方法名。改守卫/方言逻辑时在同一文件追加用例，注明对应的修复/回归编号（如现有注释"C1/I1/I11 修复的回归防线"）。
- 断言要有判别力：用 `Assert.Contains(..., ex.Message)` 断言**拒绝原因**，不要只断言异常类型——连接失败同样抛 `McpException`，仅看类型会得到恒真的假阳性用例。
- **真实数据库集成测试**（`MySqlIntegrationTests` / `PostgreSqlIntegrationTests`）：类上标 `[Trait("Category","Integration")]`，连接串由 `SQLSERVERMCP_IT_*` 环境变量提供，经 `IntegrationDatabase` 夹具建库服务；**缺环境变量时抛中文异常而非跳过**（静默跳过会制造假绿）。表名用 `UniqueTableName` 生成并在 `finally` 中 `TryDropTableAsync` 清理——测试不得污染既有库。
- **集成测试里的数据值用 ASCII**：MySQL 5.7 的库默认字符集是 latin1，写中文字面量会得到 `1366 Incorrect string value`，与要验证的事务/守卫语义无关，会变成环境相关的假失败。
- 已知限制（写进行为预期，非 bug）：演练模式回滚仍消耗 IDENTITY 值；`CREATE DATABASE` 等实例级语句无法事务回滚，靠黑名单前置拦截；MySQL 的 `group_concat_max_len`（默认 1024）会截断 `list_indexes` 的键列拼接；含分号的例程体（`CREATE PROCEDURE ... BEGIN ... ; END`）会先被单语句校验拒绝。
- **临时测试脚本陷阱**：在项目根目录用 `dotnet run --file xxx.cs` 建临时库后，**必须立即删除该 .cs 文件**——主项目会把它当普通源文件编译（`#:` 指令导致 CS9298 构建失败）。

## 手动冒烟验证（不依赖 MCP 客户端）

`dotnet test` 只覆盖无连接逻辑；验证真实数据库行为（写全流程、方言分支、导出、审计落盘）时用本配方：

```powershell
dotnet build -o bin-test     # 绕开被运行实例锁定的 bin/
```

用 `Start-Process` 启动 `bin-test\SqlServerMcp.dll`（重定向 stdin/stdout/stderr），再用 `StreamWriter`（`UTF8Encoding($false)`，**逐行写入 + Flush + 每行间隔约 1 秒**）依次写入 `initialize` → `notifications/initialized` → `tools/call …`。

要点：不要整体管道重定向 JSON 行（进程启动即读取，会丢消息）；解析输出用 `ConvertFrom-Json` 比正则可靠；验证拒绝类行为时**核对拒绝原因文本**，避免把"连接失败/其他规则拦截"误判成目标规则生效。

## 平台与仓库

- **Windows / macOS 差异**：配置中的项目路径 Windows 需 `\\` 转义，Unix 用 `/` 且无需转义；Unix 发布产物是**无扩展名**的 `publish/SqlServerMcp`，需 `chmod +x`；macOS 无本地 SQL Server（连远程实例或 Docker 容器），MySQL / PostgreSQL / SQLite 可原生连接。README「接入 CodeBuddy」含两平台完整示例。
- **远端 `origin` 是公开仓库** `github.com/wolegecaoai/mcp-sql.git`：提交并推送即公开可见。`appsettings.json`、`logs/`、`publish/`、`bin/`、`obj/`、`.codebuddy/` 均已在 `.gitignore` 中；推送前可用 `git ls-files | Select-String appsettings` 复核。
