@echo off
rem Double-click launcher for Winland. Runs start-winland.ps1 (which self-elevates with one UAC
rem prompt and starts both daemons). Keep this next to start-winland.ps1 and the Winland exes.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-winland.ps1" %*
