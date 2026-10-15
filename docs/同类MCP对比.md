# 同类数据库 MCP Server 对比分析

> 本文档对 GitHub 上主流的「数据库类 MCP Server」做客观对比，提炼**本项目（.NET 10 本地多库 MCP Server）**相对同类的优点，并给出可借鉴点与落地建议。
> 全文中文；仓库名 / 技术名保留英文原词。对比数据来源于各仓库 README（抓取于 2026-10），如官方后续调整以仓库现行文档为准。
> 本文为纯分析文档，**不改动任何源码、配置或测试**。

---

## 1. 背景与对比对象清单

本项目定位：本地 stdio MCP Server，基于 `ModelContextProtocol` C# SDK，让 AI 客户端操作**已授权**的多数据库（SQL Server / MySQL / PostgreSQL / SQLite），四类库均支持读写，写操作带二次确认演练，含方言守卫与审计日志。

选取对比对象时遵循「同类 + 主流」原则：

| 对象 | 类型 | 说明 |
|---|---|---|
| **本项目**（`.NET 10 MCP`） | 多库 all-in-one（本地） | 被对比方，SQL Server/MySQL/PostgreSQL/SQLite，读写 + 演练确认 |
| 官方参考实现 `modelcontextprotocol/server-postgres` `-mysql` `-sqlite` | 单库参考实现（已归档） | Anthropic 维护，TypeScript，以只读为主 |
| `apisql-dev/apisql-mcp` | 多库 all-in-one（远程/本地） | TypeScript/Node，15+ 数据库，完整 DDL/DML，平台网关鉴权 |
| `ttcc666/DatabaseMcpServer` | 多库 all-in-one（本地） | **C#/.NET 9**，19 种库含国产信创，约 55 个工具 |
| `chenlinyang/mcp-server-mysql` | 单库增强（只读） | TypeScript，增强型 MySQL 只读访问 |

> 说明：`wong2/mcp-cli` 经核查实为 **MCP 客户端/检查器**（inspector），并非数据库 server，故不进入 server 横向对比，仅作相邻工具在文末提及。`executeautomation/mcp-database-server` 与官方 `server-mysql` 在抓取时路径已失效（官方主仓库将 DB server 转入 `servers-archived`），以官方 `server-postgres` 代表「官方参考实现」一类。

评估标尺采用 MCP 质量维度（工具命名/可发现性、错误可操作性、tool annotations、响应归一化、安全护栏、可观测性/审计），见 [mcp-builder](https://github.com/modelcontextprotocol) 最佳实践。

---

## 2. 横向对比矩阵

| 维度 | 本项目（.NET 10 MCP） | 官方参考 `server-*` | `apisql-mcp` | `ttcc666/DatabaseMcpServer` | `chenlinyang/mcp-server-mysql` |
|---|---|---|---|---|---|
| 实现语言/运行时 | C# / .NET 10 | TypeScript / Node | TypeScript / Node | **C# / .NET 9** | TypeScript / Node |
| 传输方式 | stdio | stdio | **stdio + Streamable HTTP** | stdio（另带 `-web` 本地控制台） | stdio |
| 支持数据库 | 4 种（SQL Server/MySQL/PostgreSQL/SQLite） | 单库（pg/mysql/sqlite 各一） | **15+**（含 Oracle/TiDB/StarRocks/Doris/DuckDB/OceanBase/达梦/Trino 等） | **19 种**（含达梦/Kingbase/GaussDB/PolarDB/Vastbase 等信创） | 仅 MySQL |
| 工具数量 | 12（9 只读 + 2 写 + 1 审计） | 1（`query`）+ schema 资源 | 1（`execute_sql`） | **约 55** | 少量（探查 + 查询 + 分析） |
| 读能力 | 库/表/索引/视图/存储过程/列结构 + SELECT + 导出 | 仅 SELECT（schema 走 Resource） | 完整 SQL（SELECT/CTE/JOIN） | 完整（含标量/批量/存在性检查） | SELECT + schema 探查 + 分析 |
| 写能力 | INSERT/UPDATE/DELETE + DDL（全四类） | **无（强制只读事务）** | 完整 DML + DDL | 完整 DML + DDL + 存储过程 | **无（只读）** |
| 写安全机制 | **两阶段：演练回滚 + `confirm=true` 提交** | 事务级 READ ONLY | 平台 API Key + IP 白名单 + 最小权限 + 平台审计 | 危险操作检测 + `enableDangerousOperations` 开关（默认 false）+ 参数化 | 无（设计即只读） |
| SQL 危险语句防护 | **守卫下沉 + 剥离注释/字符串字面量匹配 + CTE 穿透 + SELECT INTO 拦截 + 例程整体拒绝(CR-1) + 无 WHERE 警告** | 无（靠只读事务兜底） | 依赖平台策略 | 关键词黑名单（DROP/TRUNCATE/ALTER/无 WHERE）+ 开关 | 无（只读） |
| 方言处理架构 | **四 Provider 路由，方言 SQL 仅在 Provider 生成，服务层不拼 SQL** | 单库无需路由 | 单 `execute_sql` 入口，靠驱动兼容 | 按 `dbType` 路由 + SqlSugar ORM | 单库 |
| 响应格式 / Token 效率 | **三形态 envelope/compact/rows，compact 省约一半 token；截断双闸** | 文本/资源，未强调 | 查询结果返回，未强调归一 | 结构化 JSON，未强调 | 未强调 |
| tool annotations | **完备（ReadOnly/Idempotent/Destructive 均显式标注）** | 未强调 | 未强调 | 未强调 | 未强调 |
| 审计日志 | **本地落盘 `logs/audit-*.log`（SQL 仅存 SHA256 + 前 200 字符）** | 无 | 平台侧审计（需外发） | 未明确本地审计 | 无 |
| 配置 / 授权 | `appsettings.json` 白名单 + `AllowWrite` 总开关 + 环境变量覆盖 | 连接串直传 | 环境变量（API Key/URL）经平台网关 | `databases.json`（路径可配，支持热监听） | 配置项 |
| 单语句限制 | **有（每条工具仅单条 SQL）** | 单条 query | 单条 `execute_sql` | 未强调 | 单条 |
| 测试 / CI | **xUnit 表驱动 333 单测 + service 容器集成测试 + 四平台发布** | 参考实现，无生产测试 | 平台托管 | 未详述 | 未详述 |
| 国产信创支持 | 无 | 无 | 部分（达梦/OceanBase） | **强（多种信创库）** | 无 |
| 启动自检 | **配置校验 + 连通性预检，只告警不阻断** | 无 | 无 | CLI `doctor` 诊断 | 无 |

---

## 3. 本项目相对同类的优点

以下为**本项目已有且在本对比中领先**的设计，按 MCP 质量维度归类：

### 3.1 安全护栏（最核心差异）
- **两阶段写确认（演练 + 提交）**：`confirm=false` 时在事务内执行后回滚，返回预估影响行数；`confirm=true` 才提交。相比 `ttcc666` 的「危险操作开关」与 `apisql-mcp` 的「平台策略」，本项目的确认发生在**进程内、事务级、可被 AI 客户端逐步验证**，不依赖外部平台，也不靠一个全局开关放行所有高危操作。
- **方言 fail-closed**：对 `SupportsTransactionalDdl=false` 的库（MySQL）DDL，**在建连之前**就判定不可事务化，`confirm=false` 时直接跳过执行并提示——根绝「以为回滚了、实际已隐式提交」这类静默数据破坏。本对比中未见其他方案做此区分。
- **守卫下沉 + 字面量剥离匹配**：所有写入口统一走 `DatabaseService.ValidateWriteStatement`，且匹配基于**剥离注释与字符串字面量后**的 SQL，规避「危险词藏在字符串/注释里绕过」的经典缺陷；`WITH(CTE)` 穿透、无 WHERE 的 UPDATE/DELETE 显式警告、例程/触发器/程序集整体拒绝（CR-1）。`ttcc666` 的危险操作检测为关键词黑名单，未说明是否剥离字面量，**存在绕过风险**。

### 3.2 响应归一化与 Token 效率
- **三形态 `envelope` / `compact` / `rows`**：`compact` 列名只输出一次，宽表省约一半 token；截断由行数（`maxRows` 上限 5000）与字符预算（`MaxResponseChars`）双闸控制，提示统一在 JSON 信封内，绝不把散文拼在 JSON 之后（避免调用方 `JSON.parse` 失败）。同类方案中 `apisql-mcp` / `ttcc666` 均未强调响应归一化，长结果更易撑爆上下文。

### 3.3 工具语义与可观测性
- **tool annotations 完备**：所有只读工具标 `ReadOnly=true, Idempotent=true`，写工具标 `Destructive=true, Idempotent=false`。这让支持 annotations 的客户端能自动区分安全/危险操作并给出 UI 提示——官方参考与多数社区 server 未设。
- **本地审计日志**：写提交/演练、守卫拒绝、执行失败均落 `<配置目录>/logs/audit-YYYYMMDD.log`（JSON 行、UTC、SQL 仅存 SHA256 + 前 200 字符预览），且 `query_audit_log` **不接受任何路径参数**（防任意文件读取）。`apisql-mcp` 的审计依赖其平台，数据需外发；`ttcc666` 未明确本地审计。
- **启动自检只告警不阻断**：配置问题输出 stderr 警告而非退出，避免 stdio 服务启动即崩导致客户端拿不到诊断。

### 3.4 架构清晰
- **Provider 分层契约**：Tools 只做参数校验/审计/中文文案；方言 SQL 全在 Provider 生成；`DatabaseService` 从不拼方言 SQL。新增一种方言只需加 Provider，不动服务层与工具层。对比 `apisql-mcp` 的「单一 `execute_sql` 包打天下」，本项目对四类方言的边界更清晰、可测试。

---

## 4. 可借鉴点与落地建议（仅建议，不实现）

以下为**同类有、本项目可借鉴**的点。每条给出大致并入方式与可行性/成本评估（成本：低=数小时级、中=数天级、高=需较大重构）。

### 4.1 国产信创数据库支持
- **来源**：`ttcc666/DatabaseMcpServer` 支持达梦/Kingbase/GaussDB/PolarDB/Vastbase 等 19 种库。
- **价值**：当前仅 4 种主流库，政务/金融客户常需信创适配。
- **并入方式**：新增 `IDatabaseProvider` 实现（达梦/Oracle 系可复用部分 T-SQL 风格 SQL）+ 对应 ADO.NET 驱动 NuGet + 在 `DatabaseProviderFactory` 注册；配置白名单扩展 `dbType`。
- **可行性**：中　**成本**：中　**风险**：驱动授权与方言细节（分页、标识符转义）需逐一验证。

### 4.2 Streamable HTTP 传输
- **来源**：`apisql-mcp` 支持 `stdio` + `--transport streamable-http`。
- **价值**：stdio 仅适合本机单客户端；远程/多客户端/容器编排场景需要 HTTP 传输。
- **并入方式**：`ModelContextProtocol` SDK 已支持 `WithHttpTransport()`，可加一个启动开关在 stdio / http 间切换；需配套考虑鉴权（本项目当前靠本地白名单，HTTP 下必须加 token/网络隔离）。
- **可行性**：中　**成本**：中　**风险**：HTTP 暴露面扩大，安全模型需重新评估（不能照搬本地白名单）。

### 4.3 参数化查询入口（防注入增强）
- **来源**：`ttcc666/DatabaseMcpServer` 强调 `@age` 绑定参数化查询。
- **价值**：本项目靠「字面量剥离 + 单语句限制」缓解注入，但工具只接收整段 SQL 字符串，调用方若拼出含用户输入的 SQL 仍有风险。
- **并入方式**：新增如 `execute_query_params`（接受 `sql` + 参数集合）或在现有工具加可选 `parameters` 形参，透传到底层 `DbCommand` 参数化执行。
- **可行性**：中　**成本**：中　**风险**：需保持守卫对参数化后的最终 SQL 仍然可见（避免参数化绕过黑名单）。

### 4.4 配置热加载 / 更友好的配置管理
- **来源**：`ttcc666/DatabaseMcpServer` 的 `enableMonitorConfig` 监听 `databases.json` 变化、CLI `doctor` 诊断。
- **价值**：本项目配置为启动快照（刻意不热更新，见 AGENTS.md），改库需重启。长驻实例下运维略繁琐。
- **并入方式**：可选增加「定时重载白名单」或复用 `reloadOnChange: true`（已在 `AddJsonFile` 开启，但 `IOptions` 是快照）；CLI `doctor` 可复用现有 `StartupDiagnostics` 输出。
- **可行性**：低–中　**成本**：低　**风险**：热更新会改变「快照可安全缓存」的既定契约，需谨慎。

### 4.5 工具粒度补充（健康检查 / 表存在性 / 更多导出）
- **来源**：`ttcc666/DatabaseMcpServer` 约 55 个工具（含 `health_check`、`is_any_table`、`switch_database` 等）。
- **价值**：本项目 12 个工具偏粗，`health_check`/`is_any_table` 这类高频小工具能减少无效大查询。
- **并入方式**：新增 `health_check`（复用 `test_connection`）、`table_exists` 等轻量只读工具；导出格式可加 `xlsx`。
- **可行性**：低　**成本**：低–中　**风险**：工具过多会增大 AI 客户端工具发现成本，需权衡（mcp-builder 建议优先「全面覆盖 + 清晰命名」）。

### 4.6 连接串密码脱敏
- **来源**：`ttcc666/DatabaseMcpServer` 在日志中将 `Password=****` 脱敏。
- **价值**：本项目审计日志已不存完整 SQL，但连接失败/启动自检的错误提示若回显连接串，可能泄露凭据。
- **并入方式**：在 `StartupDiagnostics` 与异常文案中统一对连接串做脱敏（正则替换 `Password=...;` / `Pwd=...`）。
- **可行性**：低　**成本**：低　**风险**：低。

### 4.7 不推荐借鉴的点（明确排除）
- **单一 `execute_sql` 大工具包打天下**（如 `apisql-mcp`）：牺牲了 read/write 语义区分、tool annotations 与分层守卫，与本项目的核心安全设计相悖，**不建议采用**。
- **依赖外部平台做鉴权与审计**（如 `apisql-mcp` 的网关）：引入外发依赖与可用性单点，违背本项目「本地、已授权、数据不出域」的定位，**不建议采用**。
- **靠全局危险操作开关放行高危 DDL**（如 `ttcc666` 的 `enableDangerousOperations=true`）：过于粗粒度，不如本项目的事务级演练 + 逐语句确认，**不建议直接照搬**。

---

## 5. 结论与推荐优先级

**定位判断**：本项目在「本地、多库、强安全护栏、可审计」这一细分上，对比中最完整——尤其两阶段写确认、方言 fail-closed、字面量剥离守卫、响应归一化、tool annotations、本地审计这六项，是同类方案中少有系统化做到的。短板在**数据库覆盖广度（无信创）**与**传输形态（仅 stdio）**。

**推荐落地优先级**（按「价值 / 成本」排序）：

| 优先级 | 项 | 成本 | 理由 |
|---|---|---|---|
| P0 | 4.6 连接串密码脱敏 | 低 | 安全补强，零风险，应立即做 |
| P1 | 4.3 参数化查询入口 | 中 | 直接加固防注入，契合安全定位 |
| P1 | 4.5 工具粒度补充（health_check / table_exists / xlsx） | 低–中 | 提升易用性，可增量加 |
| P2 | 4.2 Streamable HTTP 传输 | 中 | 解锁远程/多客户端，但需重做安全模型 |
| P2 | 4.1 国产信创支持 | 中 | 高价值但依赖驱动与验证成本 |
| P3 | 4.4 配置热加载 | 低 | 便利项，需评估快照契约影响 |

> 落地「可借鉴点」时，须守住本项目安全不变量（守卫下沉、fail-closed、审计不泄露完整 SQL、响应恒为合法 JSON）。任何新增传输/库类型都不应削弱现有护栏。

---

## 附：相邻工具

- `wong2/mcp-cli`：MCP **客户端/检查器**，用于列出/调用任意 MCP server 的 Tools/Resources/Prompts，支持 stdio / SSE / Streamable HTTP 对接，与本项目是「被检查对象 vs 检查器」关系，不进入 server 对比。
