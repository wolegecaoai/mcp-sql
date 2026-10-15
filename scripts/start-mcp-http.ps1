#Requires -Version 5.1
<#
.SYNOPSIS
    守护并常驻启动 http 模式的 SqlServerMcp。
.DESCRIPTION
    http 传输下客户端只发请求、不拉起进程，服务必须自己常在。本脚本提供：

      * 单实例检查：目标端口已被占用时明确退出，避免出现两个实例互相抢端口；
      * 守护循环：进程崩溃退出、或"进程在但端口不再监听"（挂死）时自动重启，间隔 3 秒；
      * 日志落盘：生命周期事件写入 publish/logs/service-YYYYMMdd.log，每次运行的
        stdout/stderr 追加进同一文件，便于回溯"崩过几次、崩在什么日志"；
      * 停止标志：文件 publish/logs/.guardian-stop 存在时循环退出（供 -Stop / -Restart 使用）。

    运行的是 publish/ 快照：改代码或改 appsettings.json 后，必须先重新
    `dotnet publish -c Release -o publish`，再 `-Restart`，否则跑的仍是旧副本。
.PARAMETER Start
    前台守护运行（默认动作）。Ctrl+C 或关闭窗口即停止。
.PARAMETER Stop
    置停止标志并结束守护进程与服务进程。
.PARAMETER Restart
    先 Stop 再 Start。
.PARAMETER Status
    只打印端口监听、守护进程、服务进程与日志目录，不启动任何东西。
.EXAMPLE
    .\scripts\start-mcp-http.ps1                 # 前台守护运行
.EXAMPLE
    .\scripts\start-mcp-http.ps1 -Status
.EXAMPLE
    .\scripts\start-mcp-http.ps1 -Restart        # 改完配置后重启
.EXAMPLE
    .\scripts\start-mcp-http.ps1 -Stop
#>
[CmdletBinding()]
param(
    [switch]$Start,
    [switch]$Stop,
    [switch]$Restart,
    [switch]$Status
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot 'publish'
$exePath = Join-Path $publishDir 'SqlServerMcp.exe'
$logDir = Join-Path $publishDir 'logs'
$stopFlag = Join-Path $logDir '.guardian-stop'
# 用于在众多 powershell 进程里认出"守护进程自己"（避免 -Stop 误杀别的窗口）
$marker = 'start-mcp-http.ps1'

$action = if ($Status) { 'Status' } elseif ($Stop) { 'Stop' } elseif ($Restart) { 'Restart' } else { 'Start' }

if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}
$logFile = Join-Path $logDir ('service-{0}.log' -f (Get-Date -Format 'yyyyMMdd'))

function Add-Text {
    <#
        以 UTF-8（带 BOM）追加写日志。
        为什么带 BOM：本脚本由 Windows PowerShell 5.1 执行，5.1 的 Get-Content 默认按 ANSI
        解码，无 BOM 的 UTF-8 会被显示成乱码；带 BOM 才能被自动识别。
    #>
    param([string]$Text)
    [System.IO.File]::AppendAllText($logFile, $Text + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($true)))
}

function Write-Log {
    param([string]$Message)
    $line = '{0} [guardian pid={1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $PID, $Message
    Add-Text $line
    Write-Host $line
}

function Get-ConfiguredPort {
    $cfg = Join-Path $publishDir 'appsettings.json'
    if (Test-Path $cfg) {
        try {
            $json = Get-Content -Raw -Path $cfg -Encoding UTF8 | ConvertFrom-Json
            $port = $json.SqlServer.HttpPort
            if ($null -ne $port -and [int]$port -gt 0) { return [int]$port }
        } catch {
            Write-Log "解析 publish\appsettings.json 的 HttpPort 失败，回退默认端口 5000：$($_.Exception.Message)"
        }
    }
    return 5000
}

function Get-ListenerPid {
    <#
        返回占用指定端口的监听进程 PID 数组；没有监听时返回 $null。
    #>
    param([int]$Port)
    try {
        $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
        if ($null -eq $conn) { return $null }
        return @($conn | Select-Object -ExpandProperty OwningProcess -Unique)
    } catch {
        # 极端环境下没有 NetTCPIP 模块：回退 netstat 解析（仅判断有无监听，拿不到可靠 PID）
        $hit = & netstat -ano | Select-String -Pattern (":{0}\s" -f $Port)
        if ($hit) { return @(-1) }
        return $null
    }
}

function Get-GuardianProcesses {
    <#
        找出其它正在跑本脚本的 powershell 进程（排除自身）。
    #>
    $procs = Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'" -ErrorAction SilentlyContinue
    if ($null -eq $procs) { return @() }
    return @($procs | Where-Object {
        $_.CommandLine -and $_.CommandLine -like ('*{0}*' -f $marker) -and $_.ProcessId -ne $PID
    })
}

function Stop-All {
    New-Item -ItemType File -Path $stopFlag -Force | Out-Null
    Write-Log '收到停止请求：置停止标志并结束守护进程与服务进程'

    foreach ($g in (Get-GuardianProcesses)) {
        try {
            Stop-Process -Id $g.ProcessId -Force -ErrorAction Stop
            Write-Log "已结束守护进程 PID=$($g.ProcessId)"
        } catch {
            Write-Log "结束守护进程 PID=$($g.ProcessId) 失败：$($_.Exception.Message)"
        }
    }

    $exes = Get-Process -Name SqlServerMcp -ErrorAction SilentlyContinue
    foreach ($e in $exes) {
        try {
            Stop-Process -Id $e.Id -Force -ErrorAction Stop
            Write-Log "已结束服务进程 PID=$($e.Id)"
        } catch {
            Write-Log "结束服务进程 PID=$($e.Id) 失败：$($_.Exception.Message)"
        }
    }

    Remove-Item $stopFlag -Force -ErrorAction SilentlyContinue
}

function Append-RunLogs {
    <#
        把本次运行的 stdout/stderr 追加进当日服务日志，然后删除临时文件。
        注意编码：被重定向的子进程按系统 ANSI/OEM 代码页（中文 Windows 为 936/GBK）写盘，
        若不转换就直接拼进 UTF-8 日志，文件会变成混合编码。这里先按 GBK 读入再以 UTF-8 追加。
    #>
    param([string[]]$Files)
    $srcEncoding = $null
    try { $srcEncoding = [Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.ANSICodePage) } catch { $srcEncoding = $null }
    if ($null -eq $srcEncoding) { $srcEncoding = [Text.Encoding]::UTF8 }

    foreach ($f in $Files) {
        if (-not (Test-Path $f)) { continue }
        $content = $null
        try { $content = [System.IO.File]::ReadAllText($f, $srcEncoding) } catch { $content = $null }
        if ($content -and $content.Trim().Length -gt 0) {
            Add-Text ('----- {0} -----{1}{2}' -f (Split-Path $f -Leaf), [Environment]::NewLine, $content.TrimEnd())
        }
        Remove-Item $f -Force -ErrorAction SilentlyContinue
    }
}

function Start-Guardian {
    if (-not (Test-Path $exePath)) {
        Write-Log "未找到 $exePath —— http 模式运行的是 publish 快照，请先执行 dotnet publish -c Release -o publish"
        exit 1
    }

    $port = Get-ConfiguredPort
    Write-Log "守护启动：exe=$exePath 端口=$port 轮询间隔=15s 重启间隔=3s"

    if (Test-Path $stopFlag) { Remove-Item $stopFlag -Force -ErrorAction SilentlyContinue }

    while ($true) {
        if (Test-Path $stopFlag) { Write-Log '检测到停止标志，守护循环退出'; break }

        $listener = Get-ListenerPid -Port $port
        if ($listener) {
            Write-Log "端口 $port 已被占用（PID=$($listener -join ',')），为避免重复实例退出。如需接管请先 -Stop"
            break
        }

        $stamp = Get-Date -Format 'HHmmss'
        $runOut = Join-Path $logDir "run-$stamp.out.log"
        $runErr = Join-Path $logDir "run-$stamp.err.log"

        Write-Log "启动服务进程：$exePath"
        $proc = Start-Process -FilePath $exePath -WorkingDirectory $publishDir `
            -RedirectStandardOutput $runOut -RedirectStandardError $runErr `
            -PassThru -WindowStyle Hidden
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $reason = ''

        while ($true) {
            Start-Sleep -Seconds 15
            if (Test-Path $stopFlag) { $reason = 'stop-flag'; break }
            if ($proc.HasExited) { $reason = 'exited-code={0}' -f $proc.ExitCode; break }
            if (-not (Get-ListenerPid -Port $port)) { $reason = 'port-not-listening'; break }
        }

        $sw.Stop()
        Write-Log "服务进程结束（PID=$($proc.Id)，原因=$reason，运行时长=$('{0:hh\:mm\:ss}' -f $sw.Elapsed)）"
        Append-RunLogs -Files @($runOut, $runErr)

        if ($reason -eq 'stop-flag') { break }
        if ($reason -like 'exited-code=0*') {
            Write-Log '进程以退出码 0 结束，视为主动停止，守护循环退出（不自动重启）'
            break
        }
        if ($reason -eq 'port-not-listening' -and -not $proc.HasExited) {
            try {
                $proc.Kill()
                Write-Log '进程仍在但端口未监听，判定为挂死，已强杀'
            } catch {
                Write-Log "强杀挂死进程失败：$($_.Exception.Message)"
            }
        }

        Write-Log '3 秒后自动重启'
        Start-Sleep -Seconds 3
    }

    Write-Log '守护循环已退出'
}

function Show-Status {
    $port = Get-ConfiguredPort
    $listener = Get-ListenerPid -Port $port
    $guards = Get-GuardianProcesses
    $exes = Get-Process -Name SqlServerMcp -ErrorAction SilentlyContinue

    Write-Host ('端口      : {0}' -f $port)
    Write-Host ('监听状态  : {0}' -f $(if ($listener) { "LISTENING (PID=$($listener -join ','))" } else { '未监听' }))
    Write-Host ('守护进程  : {0}' -f $(if ($guards) { ($guards | ForEach-Object { "PID=$($_.ProcessId)" }) -join ', ' } else { '未运行' }))
    Write-Host ('服务进程  : {0}' -f $(if ($exes) { ($exes | ForEach-Object { "PID=$($_.Id)" }) -join ', ' } else { '未运行' }))
    Write-Host ('日志目录  : {0}' -f $logDir)
}

switch ($action) {
    'Status' { Show-Status }
    'Stop' { Stop-All }
    'Restart' { Stop-All; Start-Sleep -Seconds 2; Start-Guardian }
    default { Start-Guardian }
}
