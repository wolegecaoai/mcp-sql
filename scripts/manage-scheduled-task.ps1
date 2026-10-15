#Requires -Version 5.1
<#
.SYNOPSIS
    注册 / 注销 / 查看「SqlServerMcp http 服务」的登录时自动启动计划任务。
.DESCRIPTION
    http 传输下客户端不拉起进程，本脚本把启动器 run-guardian.cmd（内部调用
    start-mcp-http.ps1）挂到计划任务上，使用户登录后服务自动常驻（隐藏窗口运行，
    无需每次手工双击）。刻意用 .cmd 中转是为了规避"含空格路径作为参数被截断"的坑。

    任务设置要点：
      * -AtLogOn            当前用户登录即触发（不需要保存密码，也不需要管理员权限）；
      * -Hidden             运行时隐藏窗口；
      * -ExecutionTimeLimit 0  取消默认 3 天执行时限，否则任务会被系统强制结束；
      * -RestartCount 3 / -RestartInterval 1m   守护进程自身异常退出时由任务计划重试；
      * -MultipleInstances IgnoreNew  已有实例在跑时不再拉起第二个；
      * -AllowStartIfOnBatteries / -DontStopIfGoingOnBatteries  笔记本拔电源后不被挂起。

    撤销：.\scripts\manage-scheduled-task.ps1 -Unregister（一条命令，无残留）。

    已知限制：本机若以**非管理员**账户运行且组策略禁止创建计划任务，Register-ScheduledTask
    与 schtasks 都会返回 Access is denied。此时改用 manage-startup-shortcut.ps1（启动文件夹），
    无需管理员权限；或在管理员 PowerShell 里重跑本脚本。
.PARAMETER Register
    注册任务（同名任务直接覆盖）。
.PARAMETER Unregister
    注销任务。注意：这不会停止正在运行的服务，需要时再执行 start-mcp-http.ps1 -Stop。
.PARAMETER Status
    查看任务是否存在及其状态。
.EXAMPLE
    .\scripts\manage-scheduled-task.ps1 -Register
.EXAMPLE
    .\scripts\manage-scheduled-task.ps1 -Status
.EXAMPLE
    .\scripts\manage-scheduled-task.ps1 -Unregister
#>
[CmdletBinding()]
param(
    [switch]$Register,
    [switch]$Unregister,
    [switch]$Status
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$taskName = 'SqlServerMcp-HTTP'
$launcher = Join-Path $PSScriptRoot 'run-guardian.cmd'
$guardianScript = Join-Path $PSScriptRoot 'start-mcp-http.ps1'
$actionName = if ($Register) { 'Register' } elseif ($Unregister) { 'Unregister' } elseif ($Status) { 'Status' } else { 'Register' }

if (-not (Test-Path $launcher)) {
    throw "未找到启动器：$launcher"
}
if (-not (Test-Path $guardianScript)) {
    throw "未找到守护脚本：$guardianScript"
}

function Get-TaskInfo {
    Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
}

switch ($actionName) {
    'Status' {
        $t = Get-TaskInfo
        if ($null -eq $t) {
            Write-Host "计划任务 $taskName ：未注册"
        } else {
            $i = Get-ScheduledTaskInfo -TaskName $taskName
            Write-Host "计划任务 $taskName ：已注册"
            Write-Host ("  状态      : {0}" -f $t.State)
            Write-Host ("  上次运行  : {0}（结果 {1}）" -f $i.LastRunTime, $i.LastTaskResult)
            Write-Host ("  下次运行  : {0}" -f $i.NextRunTime)
            Write-Host ("  执行      : {0}" -f ($t.Actions | ForEach-Object { "$($_.Execute) $($_.Arguments)" }))
        }
    }
    'Unregister' {
        $t = Get-TaskInfo
        if ($null -eq $t) {
            Write-Host "计划任务 $taskName ：未注册，无需注销"
        } else {
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
            Write-Host "计划任务 $taskName ：已注销"
        }
    }
    default {
        <#
            直接执行 run-guardian.cmd（而非 powershell -File <含空格路径>）：
            计划任务的 Arguments 是整串交给 CreateProcess 解析的，带空格路径若不显式加引号
            会被截断；用 .cmd 启动器 + %~dp0 自定位可彻底规避（与 Start-Process 同坑）。
        #>
        $act = New-ScheduledTaskAction -Execute $launcher
        $trg = New-ScheduledTaskTrigger -AtLogOn
        $set = New-ScheduledTaskSettingsSet -Hidden `
            -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
            -ExecutionTimeLimit ([TimeSpan]::Zero) `
            -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $taskName -Action $act -Trigger $trg -Settings $set -Force | Out-Null
        Write-Host "计划任务 $taskName ：已注册（登录时以隐藏窗口运行 $launcher）"
    }
}
