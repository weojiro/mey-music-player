@echo off
setlocal
title mey music player - dev build

rem For development only: builds the code in ..\src on this PC and starts it from %LOCALAPPDATA%\MMP
set "SRC=%~dp0..\src"
set "INSTALL=%LOCALAPPDATA%\MMP"
set "DOTNET=dotnet"

echo.
echo  [1/3] Checking .NET 8 SDK...
set "HAVE=0"
for /f "delims=" %%v in ('dotnet --list-sdks 2^>nul') do (
  echo %%v | findstr /b "8." >nul && set "HAVE=1"
)
if "%HAVE%"=="0" (
  echo        Not found. Installing it with winget, this can take a few minutes...
  winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements
  if errorlevel 1 goto nosdk
  set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
)

echo  [2/3] Building...
taskkill /im "MMP.exe" /f >nul 2>&1
"%DOTNET%" publish "%SRC%\MMP.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o "%INSTALL%"
if errorlevel 1 goto failed

echo  [3/3] Starting...
start "" "%INSTALL%\MMP.exe"
exit /b 0

:nosdk
echo  Could not install the .NET 8 SDK automatically. Install it from https://dotnet.microsoft.com/download/dotnet/8.0
pause
exit /b 1

:failed
echo  The build failed. Copy the red error lines above and send them to me.
pause
exit /b 1
