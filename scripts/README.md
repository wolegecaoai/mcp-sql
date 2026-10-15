# scripts/ — http 模式常驻运维脚本

`Transport=http` 时，MCP 客户端（CodeBuddy / OpenCode 等）**只发请求、不拉起进程**，服务必须自己常在。
本目录提供守护与登录自启能力，覆盖「每次手工启动」「崩溃没人拉」「改了配置不生效」三类问题。

| 文件 | 作用 | 是否常驻 | 需要权限 |
| --- | --- | --- | --- |
| `start-mcp-http.ps1` | 守护主体：拉起服务、单实例检查、崩溃/挂死自动重启、日志落盘 | 是（前台循环） | 否 |
| `run-guardian.cmd` | 无窗口启动器，内部调用上面的 ps1 并透传参数 | 是 | 否 |
| `manage-scheduled-task.ps1` | 注册/注销登录时自动启动的计划任务 | 否（一次性） | **需要**创建计划任务的权限 |
| `manage-startup-shortcut.ps1` | 安装/卸载「启动」文件夹快捷方式（计划任务被拒时的替代） | 否（一次性） | 否 |

四个文件均**不含任何凭据**（无 ApiKey、无数据库密码），可安全提交。守护跑的是 `publish/` 快照。

## 快速上手

```powershell
cd 'd:\项目\MCP 数据库'

# 1) 看当前状态（随时可跑，不改动任何东西）
.\scripts\start-mcp-http.ps1 -Status

# 2) 后台守护运行（不要直接跑 -Start：那是前台循环，会占住你的终端）
.\scripts\run-guardian.cmd

# 3) 停止
.\scripts\start-mcp-http.ps1 -Stop
```

日常最常用的是 `-Restart`（改完代码/配置后）：

```powershell
# 先重新发布，否则跑的还是旧副本
dotnet publish -c Release -o publish
# 再 detached 重启（-Restart 是阻塞的，必须经 run-guardian.cmd 或 Start-Process 启动）
.\scripts\run-guardian.cmd -Restart
```

## start-mcp-http.ps1

```powershell
.\scripts\start-mcp-http.ps1 [-Start | -Stop | -Restart | -Status]
```

| 参数 | 行为 | 阻塞？ |
| --- | --- | --- |
| `-Status` | 打印端口、监听 PID、守护 PID、服务 PID、日志目录 | 否 |
| `-Start`（默认） | 前台守护循环，Ctrl+C 或关窗即停 | **是** |
| `-Stop` | 置停止标志 → 结束其它守护进程 → 结束所有 `SqlServerMcp` 进程 | 否 |
| `-Restart` | 先 `Stop`，等 2 秒，再进入守护循环 | **是** |
| （无参数） | 等同 `-Start` | **是** |

> `-Start` / `-Restart` 会一直循环，所以**不要**直接在终端里跑——用 `.\scripts\run-guardian.cmd [-Restart]`
> 或 `Start-Process` 启动，让它脱离当前终端。

守护循环的行为：

1. **单实例检查**：目标端口已被占用时，写日志并退出循环（不会去抢端口、不会起第二个实例）。
2. **崩溃重启**：子进程退出码非 0 → 3 秒后重拉。
3. **挂死重启**：子进程还在、但端口不再监听（15 秒轮询发现）→ 判定挂死并强杀 → 3 秒后重拉。
4. **主动停止不重启**：子进程退出码为 0 时视为主动停止，退出循环。
5. 每次运行的 stdout/stderr 会合并进当日服务日志，便于回溯"崩过几次、崩在什么日志"。

关键路径与参数（改脚本前先看这里）：

| 项 | 值 |
| --- | --- |
| 被守护的可执行文件 | `<仓库>\publish\SqlServerMcp.exe`，工作目录固定为 `publish\` |
| 端口来源 | `publish\appsettings.json` 的 `SqlServer:HttpPort`，缺失或非法时回退 5000 |
| 日志目录 | `<仓库>\publish\logs\` |
| 当日服务日志 | `service-YYYYMMdd.log`（UTF-8 带 BOM） |
| 停止标志文件 | `publish\logs\.guardian-stop`（存在时循环退出） |
| 轮询间隔 / 重启间隔 | 15 秒 / 3 秒 |

`-Status` 靠命令行里含 `start-mcp-http.ps1` 识别守护进程（排除自身），所以经 `.cmd` 或计划任务启动都能被认出来。

## run-guardian.cmd

无窗口启动器，供计划任务、启动快捷方式和 `Start-Process` 调用：

```cmd
scripts\run-guardian.cmd                 # 等同 -Start
scripts\run-guardian.cmd -Restart        # 透传给 ps1
scripts\run-guardian.cmd -Status
```

**为什么要有它**：`Start-Process -ArgumentList` 和计划任务的 `Arguments` 都不会给带空格的路径加引号，
直接把 `.ps1` 路径当参数传会被空格截断（历史上"手动点 exe 无效"的根因之一）。`.cmd` 用 `%~dp0` 自定位，
彻底绕开参数传递。因此该文件**必须保持 ASCII**（cmd 按 ANSI 读取，写中文会乱码）。

## manage-scheduled-task.ps1（方案 1：计划任务）

```powershell
.\scripts\manage-scheduled-task.ps1 -Register      # 注册（同名任务直接覆盖）
.\scripts\manage-scheduled-task.ps1 -Status
.\scripts\manage-scheduled-task.ps1 -Unregister
```

任务名 `SqlServerMcp-HTTP`，登录时以隐藏窗口运行 `run-guardian.cmd`。设置要点：

| 设置 | 值 | 为什么 |
| --- | --- | --- |
| 触发器 | `AtLogOn` | 当前用户登录即起，不需要保存密码 |
| `Hidden` | 开 | 不弹窗口 |
| `ExecutionTimeLimit` | 0（无限） | 取消默认 3 天时限，否则任务会被系统强制结束 |
| `RestartCount` / `RestartInterval` | 3 次 / 1 分钟 | 守护进程自身异常退出时由任务计划兜底 |
| `MultipleInstances` | `IgnoreNew` | 已有实例在跑时不再拉起第二个 |
| 电池相关 | 允许/不停止 | 笔记本拔电源后不被挂起 |

**已知限制**：非管理员账户且组策略禁止创建计划任务时，`Register-ScheduledTask` 与 `schtasks` 都会返回
`Access is denied`。此时改用下面的启动文件夹方案，或在**管理员** PowerShell 里重跑本脚本。

## manage-startup-shortcut.ps1（方案 2：启动文件夹）

```powershell
.\scripts\manage-startup-shortcut.ps1 -Install      # 创建快捷方式（已存在则覆盖）
.\scripts\manage-startup-shortcut.ps1 -Status
.\scripts\manage-startup-shortcut.ps1 -Remove       # 只删快捷方式，不停服务
```

在 `…\Start Menu\Programs\Startup\` 创建 `SqlServerMcp-HTTP.lnk`，目标为 `run-guardian.cmd`，
工作目录为仓库根目录，窗口样式最小化（7）。

与计划任务的差异：同样登录即起，但**与会话绑定**（注销/关机即停，重新登录后恢复）；
崩溃自愈只由守护脚本自身提供（没有计划任务的重试兜底）；会有一个最小化窗口。

## 日志怎么看

位置：`publish\logs\service-YYYYMMdd.log`（UTF-8 带 BOM，记事本/VS Code 直接打开正常；
PowerShell 5.1 的 `Get-Content` 也能正确识别）。

两类内容：

```
2026-10-15 08:33:30 [guardian pid=21072] 守护启动：exe=...\publish\SqlServerMcp.exe 端口=5000 轮询间隔=15s 重启间隔=3s
2026-10-15 08:33:31 [guardian pid=21072] 启动服务进程：...\publish\SqlServerMcp.exe
2026-10-15 08:34:02 [guardian pid=21072] 3 秒后自动重启
----- run-083429.out.log -----
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://0.0.0.0:5000
```

- `[guardian pid=N]` 开头的行是**守护脚本**写的生命周期事件（启动、结束、重启、失败原因）。
- `----- run-HHmmss.out.log -----` / `.err.log` 之后是**本次服务运行**的 stdout/stderr（已从 GBK 转码为 UTF-8）。

排障时先看有没有 `端口 <N> 已被占用`（说明已有实例）和 `未找到 ...SqlServerMcp.exe`（说明还没 publish）。

## 常见问题

| 现象 | 原因与处理 |
| --- | --- |
| 守护起来了但客户端连不上 | 客户端只发请求不拉进程：确认 `-Status` 显示 LISTENING；再看服务日志里有没有 `POST /mcp`——只有 `GET /mcp → 401` 说明客户端没发起握手，问题在客户端侧（`mcp.json` 只在其启动时读取，需重启 IDE） |
| 端口被占用、守护直接退出 | 已有实例在跑。先 `-Stop` 再启动；或用 `netstat -ano \| findstr ":5000"` 找到占用者 |
| 改了配置不生效 | 守护跑的是 `publish/` 快照：必须 `dotnet publish -c Release -o publish` 后再 `-Restart`。`bin/`、`publish/` 里的 `appsettings.json` 是拷贝，双击 exe 时工作目录在 exe 目录，会读到不含 `Transport` 的旧副本而回退成 stdio |
| 提示"未找到 SqlServerMcp.exe" | 还没发布过。执行 `dotnet publish -c Release -o publish` |
| 计划任务注册报 `Access is denied` | 权限不足，见上。改用 `manage-startup-shortcut.ps1 -Install` |
| 日志中文乱码 | 日志本身是 UTF-8 带 BOM；若用 `Get-Content` 仍乱码，说明读到了 5.1 之前的缓存文件，重新打开即可 |
| 想彻底停掉并取消自启 | `.\scripts\start-mcp-http.ps1 -Stop`，再 `.\scripts\manage-startup-shortcut.ps1 -Remove`（或 `manage-scheduled-task.ps1 -Unregister`） |

## 维护约定

- 新增/修改 `.ps1`：**必须保存为 UTF-8 带 BOM**（Windows PowerShell 5.1 按 ANSI 解析，无 BOM 的中文注释会直接导致解析失败）。
- 修改 `.cmd`：**必须保持 ASCII**（cmd 按 ANSI 读取）。
- 不要把 ApiKey、连接串、密码写进本目录任何文件；这些脚本只依赖仓库内的 `publish/` 快照与配置。
