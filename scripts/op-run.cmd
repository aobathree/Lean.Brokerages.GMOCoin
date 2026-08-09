@echo off
rem Windows launcher for op-run.ps1: runs any command with GMOCOIN_API_KEY /
rem GMOCOIN_API_SECRET resolved from 1Password (child-process env vars only).
rem Works from cmd.exe and PowerShell regardless of the ExecutionPolicy setting.
rem Usage: scripts\op-run.cmd dotnet run --project QuantConnect.GMOCoinBrokerage/tools/AssetsCheck
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0op-run.ps1" %*
exit /b %ERRORLEVEL%
