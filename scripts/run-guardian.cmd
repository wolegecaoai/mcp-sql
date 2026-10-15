@echo off
rem Launcher for the http-mode guardian (scripts\start-mcp-http.ps1).
rem Uses %~dp0 to resolve its own directory so no path is passed as an argument:
rem Start-Process and Task Scheduler do not auto-quote paths containing spaces.
rem %* forwards extra switches (e.g. -Restart / -Status) to the PowerShell script.
rem Keep this file ASCII-only: cmd reads it as ANSI, UTF-8 Chinese would be garbled.
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "%~dp0start-mcp-http.ps1" %*
endlocal
