@echo off
rem WeChatBridge portable first run.
rem Pure ASCII on purpose: cmd.exe reads a .bat in the system ANSI code page,
rem so any Chinese in this file would be mangled before PowerShell ever sees it.
rem The UTF-8-BOM PowerShell script next to this file does the real work.
setlocal
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0FirstRun.ps1" %*
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" pause
exit /b %EXITCODE%
