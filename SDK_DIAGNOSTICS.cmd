@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0"
call "%~dp0build-tools\FindDotNet10.cmd"
set "RC=%ERRORLEVEL%"
echo.
echo See build-logs\R15_dotnet_probe.log and R15_dotnet_candidate.log.
if "%RC%"=="0" echo [PASS] SDK selected. You can run BUILD.cmd now.
if not "%RC%"=="0" echo [INFO] If SDK is installed elsewhere, set SUNRISE_DOTNET to its dotnet.exe path.
if /I not "%~1"=="--no-pause" pause
exit /b %RC%
