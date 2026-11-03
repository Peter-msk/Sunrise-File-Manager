@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0..\.."
if errorlevel 1 exit /b 1
set "ROOT=%CD%"
set "LOG_DIR=%ROOT%\validation\windows-split-interaction"
if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"
set "SUNRISE_DOTNET_RESOLVED="
call "%ROOT%\build-tools\FindDotNet10.cmd" > "%LOG_DIR%\sdk.log" 2>&1
if errorlevel 1 goto :sdk_failed
if not defined SUNRISE_DOTNET_RESOLVED goto :sdk_failed
echo [CHECK] Isolated WPF interaction: all 14 layouts, 1-4 panes.
echo [CHECK] Only generated temporary folders are used. No user session is saved.
echo [CHECK] This is opt-in and is not part of BUILD_ON_WINDOWS_COMPANY.cmd.
if exist "%LOG_DIR%\UI_RESULT.json" move /y "%LOG_DIR%\UI_RESULT.json" "%LOG_DIR%\UI_RESULT.previous.json" >nul
"%SUNRISE_DOTNET_RESOLVED%" run --project "%ROOT%\source\tests\UiSmokeTests\UiSmokeTests.csproj" --configuration Release -- --split-interaction-only "%LOG_DIR%"
set "CHECK_EXIT_CODE=%ERRORLEVEL%"
if not exist "%LOG_DIR%\UI_RESULT.json" set "CHECK_EXIT_CODE=1"
if exist "%LOG_DIR%\UI_RESULT.json" type "%LOG_DIR%\UI_RESULT.json"
echo.
echo [CHECK] Exit code: %CHECK_EXIT_CODE%
echo [CHECK] Results: validation\windows-split-interaction\UI_RESULT.json
if /I not "%~1"=="--no-pause" pause
exit /b %CHECK_EXIT_CODE%
:sdk_failed
echo [ERROR] .NET 10 SDK not found. See validation\windows-split-interaction\sdk.log.
if /I not "%~1"=="--no-pause" pause
exit /b 1
