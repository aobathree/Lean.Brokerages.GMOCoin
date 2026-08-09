@echo off
rem Windows launcher for run-e2e.ps1 (full-engine live E2E test; places one
rem real minimum-lot order and cancels it automatically).
rem Works from cmd.exe and PowerShell regardless of the ExecutionPolicy setting.
rem Usage: scripts\run-e2e.cmd
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-e2e.ps1" %*
exit /b %ERRORLEVEL%
