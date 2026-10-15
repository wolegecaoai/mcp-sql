#Requires -Version 5.1
<#
.SYNOPSIS
    安装 / 卸载 / 查看「登录时自动启动 http 守护进程」的启动文件夹快捷方式。
.DESCRIPTION
    计划任务方案（manage-scheduled-task.ps1）需要创建计划任务的权限；若本机策略禁止
    （非管理员账户常被拒：Register-ScheduledTask / schtasks 均返回 Access denied），
    用本脚本把启动器挂到「启动」文件夹作为等效替代。

    与计划任务的差异：
      * 同样登录即起，但**会话相关**——注销/关机即停，重启登录后恢复；
      * 崩溃自愈只由守护脚本自身提供（没有计划任务的 RestartCount 兜底）；
      * 会有一个最小化的窗口（计划任务可完全隐藏）。

    撤销：.\scripts\manage-startup-shortcut.ps1 -Remove（只删快捷方式，不碰服务进程）。
.PARAMETER Install
    创建快捷方式（已存在则覆盖）。
.PARAMETER Remove
    删除快捷方式。
.PARAMETER Status
    查看快捷方式是否存在及其指向。
.EXAMPLE
    .\scripts\manage-startup-shortcut.ps1 -Install
.EXAMPLE
    .\scripts\manage-startup-shortcut.ps1 -Status
.EXAMPLE
    .\scripts\manage-startup-shortcut.ps1 -Remove
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Remove,
    [switch]$Status
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$linkName = 'SqlServerMcp-HTTP.lnk'
$startupDir = [Environment]::GetFolderPath('Startup')
$linkPath = Join-Path $startupDir $linkName
$launcher = Join-Path $PSScriptRoot 'run-guardian.cmd'
$repoRoot = Split-Path -Parent $PSScriptRoot

$actionName = if ($Install) { 'Install' } elseif ($Remove) { 'Remove' } elseif ($Status) { 'Status' } else { 'Install' }

if (-not (Test-Path $launcher)) {
    throw "未找到启动器：$launcher"
}

switch ($actionName) {
    'Status' {
        if (Test-Path $linkPath) {
            $sh = New-Object -ComObject WScript.Shell
            $lnk = $sh.CreateShortcut($linkPath)
            Write-Host "启动快捷方式：已安装"
            Write-Host ("  路径    : {0}" -f $linkPath)
            Write-Host ("  目标    : {0}" -f $lnk.TargetPath)
            Write-Host ("  工作目录: {0}" -f $lnk.WorkingDirectory)
            Write-Host ("  窗口样式: {0}（7=最小化）" -f $lnk.WindowStyle)
        } else {
            Write-Host "启动快捷方式：未安装（$linkPath）"
        }
    }
    'Remove' {
        if (Test-Path $linkPath) {
            Remove-Item $linkPath -Force
            Write-Host "启动快捷方式：已删除（$linkPath）"
        } else {
            Write-Host "启动快捷方式：未安装，无需删除"
        }
    }
    default {
        $sh = New-Object -ComObject WScript.Shell
        $lnk = $sh.CreateShortcut($linkPath)
        $lnk.TargetPath = $launcher
        $lnk.WorkingDirectory = $repoRoot
        $lnk.WindowStyle = 7
        $lnk.Description = 'SqlServerMcp http 模式守护进程（scripts\run-guardian.cmd）'
        $lnk.Save()
        Write-Host "启动快捷方式：已安装（$linkPath → $launcher）"
    }
}
