@echo off
set "EXE=%LOCALAPPDATA%\MMP\MMP.exe"
if not exist "%EXE%" (
  echo mey music player is not installed yet. Run "MMP Launcher.bat" first.
  pause
  exit /b 1
)
taskkill /im "MMP.exe" /f >nul 2>&1
start "" "%EXE%" --debug
