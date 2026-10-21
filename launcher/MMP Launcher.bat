@echo off
setlocal EnableExtensions

if /i "%~1"=="/run" goto run

rem Install / repair: everything is done by launcher.ps1 (English console text, colors, progress bars)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0launcher.ps1"
exit /b

:run
rem Normal start (desktop shortcut / Start with Windows). MMP.exe has the .NET runtime inside.
start "" "%~dp0MMP.exe"
exit /b 0
