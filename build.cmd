@echo off
rem Build all sources without replacing the installed executable.
rem Usage: build.cmd [-OutputPath "D:\folder\RcloneOssManager.exe"]
rem Deploy: build.cmd -DeployDir "D:\tools\RcloneOssManager"  (stops old instance, copies, restarts)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %errorlevel%
