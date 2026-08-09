@echo off
rem Windows launcher for setup-1password.ps1.
rem Works from cmd.exe and PowerShell regardless of the ExecutionPolicy setting.
rem Usage: scripts\setup-1password.cmd [-Vault <vault>] [-Item <item>]
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-1password.ps1" %*
exit /b %ERRORLEVEL%
