@echo off
setlocal EnableExtensions

if /i "%~1"=="/run" goto run

rem Install / repair: everything is done by launcher.ps1 (English console text, colors, progress bars)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0launcher.ps1"
exit /b

:run
rem Normal start (desktop shortcut / Start with Windows): quick check that the .NET Desktop Runtime 8 is there
if exist "%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\8.*" (
  start "" "%~dp0MMP.exe"
  exit /b 0
)
rem Not found in the usual place: the script checks more carefully and shows a message in the user's language
powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0launcher.ps1" -Run
exit /b 0
