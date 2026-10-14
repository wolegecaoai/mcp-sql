# AGENTS.md

.NET 8 C# 项目：本地 stdio MCP Server（`ModelContextProtocol` SDK），让 AI 客户端操作已授权的多数据库。四类数据库（SQL Server / MySQL / PostgreSQL / SQLite）均支持读写，写操作带 confirm 二次确认演练；方言差异（如 MySQL DDL 隐式提交不可演练）由各 Provider 的能力标志控制。含写操作审计日志。

**语言约定**：README、代码注释、工具 Description 全部为中文，新增文档/注释保持一致。

## 命令

```powershell
dotnet build                              # 构建（无 .sln，根目录直接构建主项目）
dotnet run --no-build                     # 运行；必须加 --no-build，避免构建输出污染 stdio 通道
dotnet test tests/SqlServerMcp.Tests      # 跑测试（纯单元测试，无需连接任何数据库）
dotnet publish -c Release -o publish      # 发布快照；publish/ 目录不是热更新，改代码/配置后必须重新 publish
```

验证顺序：`dotnet build` → `dotnet test`。项目无 lint/format/typecheck 配置。

## 结构

无解决方案文件，两个 csproj 并存；主项目用 `<Compile Remove="tests\**\*.cs" />` 排除测试代码——**不要**在根目录随手 `dotnet build` 测试目录外的其他 cs 文件而不检查编译归属。

```
Program.cs                    # 入口：Generic Host + stdio 传输 + DI；所有日志走 stderr
Configuration/SqlServerOptions.cs  # 强类型配置，绑定 "SqlServer" 节
Services/
  DatabaseService.cs          # 白名单解析、Provider 路由、通用执行；ExecuteReadOnlyQueryAsync / ExecuteInTransactionAsync 是唯一受认可的查询/写入入口
  SqlGuardService.cs          # SQL 安全守卫（见下「安全不变量」）
  DatabaseProviderFactory.cs  # 按 Type 字符串创建 Provider（大小写不敏感，缺省 sqlserver）
  TableNameParser.cs          # 引号标识符感知的表名解析（四 Provider 共用）
  ExportFormatter.cs          # CSV / Markdown / JSON 导出纯函数（含公式注入防护）
  AuditLogger.cs              # 写操作与守卫拒绝的 JSON 行审计日志（<配置目录>/logs/）
  Providers/*.cs              # 四种数据库方言实现
Tools/
  ReadTools.cs                # 9 个只读工具（7 探查/查询 + test_connection + export_query_result）
  WriteTools.cs               # 2 个写工具：execute_non_query / execute_ddl
tests/SqlServerMcp.Tests/     # xUnit 表驱动测试（守卫 / Provider 方言 / 导出 / 审计 / SQLite 写路径集成）
```

数据流：Tool → `DatabaseService.GetProvider(database)`（白名单校验）→ Provider 生成方言 SQL → `DatabaseService` 经 `DbConnection` 执行。

## 关键机制（易踩坑）

- **stdout 是 JSON-RPC 协议通道**：任何 Console 输出/新日志都必须进 stderr（`Program.cs` 已设 `LogToStandardErrorThreshold = LogLevel.Trace`）。`dotnet run` 不加 `--no-build` 会因构建输出破坏协议。
- **配置加载顺序**（`Program.cs` `ResolveConfigDirectory`）：优先 CWD 的 `appsettings.json`，但仅当其含 `"SqlServer"` 节才采用，否则回退到 `AppContext.BaseDirectory` 的副本。环境变量覆盖用双下划线：`SqlServer__Databases__<别名>__ConnectionString`。
- **`appsettings.json` 已被 `.gitignore` 排除**（含真实连接密码），`bin/`、`publish/` 中的副本同样含密码。**不要**提交它，也不要用模板覆盖用户现有配置。其中 `OnlineQuotationSystem` 的库名连接串里含零宽字符 U+200C，是刻意保留的（真实库名如此），编辑时勿"清理"。
- **`nuget.config` 必须保留**：`<clear />` 锁定 nuget.org，因为本机全局 NuGet 配置存在不可达源会导致还原失败。
- **新增数据库类型**：实现 `IDatabaseProvider`（`SupportsWrite` / `SupportsTransactionalDdl` / `ExtraDangerousKeywords` / `BuildServerVersionSql` 四项能力标志 + 目录查询）→ 在 `DatabaseProviderFactory` 注册 → 更新 README 与 `ProviderTests`。
- **`dotnet run` 与 `--no-build`**：本地手工测试时建议加 `--no-build`（避免构建输出污染 stdio）；作为 MCP 客户端配置时**不要**加（否则改代码后重启不会重编译）。
- **`publish/` 是快照**：OpenCode 侧配置指向 `publish\SqlServerMcp.exe`，改代码或配置后必须重新 `dotnet publish -c Release -o publish` 才生效（CodeBuddy 侧是 `dotnet run`，重启自动编译）。

## 安全不变量（改动 SqlGuardService / WriteTools 前必读）

这些是项目的核心契约，测试（`SqlGuardServiceTests`）即其回归防线，破坏任何一条都算重大回归：

- **写操作两阶段**：`confirm=false`（默认）= 事务内执行后 ROLLBACK 返回预估影响行数（演练）；`confirm=true` 才 COMMIT。
- **守卫基于剥离注释与字符串字面量后的 SQL** 匹配，新增检查沿用同一前提，避免误伤字面量。剥离器对 `/*!`（MySQL 可执行注释）与未终止字面量**一律 fail-closed 拒绝**——守卫看到的 SQL 必须与实际执行的一致。
- **`WITH`（CTE）穿透**：按括号深度跳过 CTE 定义，识别其后的主语句类型，防止 `WITH cte AS (...) INSERT/UPDATE/DELETE` 绕过写确认。
- **`SELECT ... INTO` 按写操作拦截**（查询路径建表写数据）。
- 单语句限制、危险关键词黑名单 = 全局（配置可调，含 `DROP DATABASE`、`GRANT`、`xp_cmdshell`、跨方言数据外发函数）+ 各 Provider 的 `ExtraDangerousKeywords`（例程/触发器/程序集/角色等创建语句——函数体可藏匿危险关键词，黑名单看不见），二者经 `DatabaseService.GetEffectiveDangerousKeywords` 合并；无 WHERE 的 UPDATE/DELETE 需显式确认。
- **守卫下沉是结构性防线**：`DatabaseService.AssertReadOnlyQuery`/`ExecuteReadOnlyQueryAsync`（只读）与 `ValidateWriteStatement`/`ExecuteInTransactionAsync`（写入，`allowedTypes` 白名单**必填**，不传编译不过）内部各自完成守卫校验；`ExecuteQueryAsync` 与 `OpenConnectionAsync` 均为 `internal`（后者是"裸连接"逃逸口），仅程序集内部与测试（`InternalsVisibleTo`）可用——生产路径的目录/版本工具直接调用 `ExecuteQueryAsync` 执行**自身生成的** SQL，用户 SQL 一律走上面的守卫入口。
- **例程/触发器/程序集创建语句整体拒绝（CR-1）**：`IDatabaseProvider.RoutineCreationPattern` 用方言正则匹配（覆盖 `CREATE OR ALTER`/`CREATE OR REPLACE`/`ALTER`/`TEMP` 等变体——固定短语会被修饰词绕过），原因见上：语句体可藏危险词且可提权。**新增方言时若用固定短语黑名单，必须同时提供该正则。**
- **方言 fail-closed**：`SupportsTransactionalDdl=false` 的库（MySQL）DDL 在演练模式**不执行**并明确提示，且该判定发生在建立连接之前；禁止对其开事务执行 DDL（隐式提交会导致"已提交却报错"）。
- **审计日志**：写操作提交/演练、守卫拒绝、写执行失败均落 `<配置目录>/logs/audit-YYYYMMDD.log`（JSON 行、UTC 时间戳、SQL 仅存 SHA256 + 前 200 字符预览）；写日志失败不得中断主流程。
- **导出安全**：CSV 单元格以 `=` `+` `-` `@` 开头（含前置制表符绕过）时前置 `'`，防 Excel/WPS 公式注入；合法负数不误伤。
- 各 Provider 的目录查询 SQL 输出**列别名已归一化**（`approx_rows`、`column_name`、`pk_name`、`fk_name`、`key_columns`、`definition_head` 等），`ProviderTests` 断言此契约——工具层按这些别名取值。

## 测试

- 业务逻辑测试无需任何数据库连接（含方言相关的 fail-closed 分支——它们都在建连之前）；仅 `SqliteWriteIntegrationTests` 使用临时 SQLite 文件验证真实事务路径。
- 风格为 xUnit `[Theory]` + `[InlineData]` 表驱动，中文方法名。改守卫/方言逻辑时在同一文件追加用例，注明对应的修复/回归编号（如现有注释"C1/I1/I11 修复的回归防线"）。
- 已知限制（写进行为预期，非 bug）：演练模式回滚仍消耗 IDENTITY 值；`CREATE DATABASE` 等实例级语句无法事务回滚，靠黑名单前置拦截；MySQL 的 `group_concat_max_len`（默认 1024）会截断 `list_indexes` 的键列拼接；含分号的例程体（`CREATE PROCEDURE ... BEGIN ... ; END`）会先被单语句校验拒绝。
- **临时测试脚本陷阱**：在项目根目录用 `dotnet run --file xxx.cs` 建临时库后，**必须立即删除该 .cs 文件**——主项目会把它当普通源文件编译（`#:` 指令导致 CS9298 构建失败）。
