@echo off
rem Windows launcher for run-live.ps1: runs the momentum rotation algorithm
rem LIVE with REAL MONEY on the local machine, until Ctrl+C.
rem Works from cmd.exe and PowerShell regardless of the ExecutionPolicy setting.
rem Usage: scripts\run-live.cmd
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-live.ps1" %*
exit /b %ERRORLEVEL%
