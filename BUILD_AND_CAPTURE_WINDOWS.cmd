@echo off
setlocal EnableExtensions
cd /d "%~dp0"
if exist "%~dp0COMPANY_SOURCE_ONLY.flag" (
    echo [COMPANY POLICY] UI smoke tests are disabled in this distribution.
    echo [COMPANY POLICY] Run BUILD_ON_WINDOWS_COMPANY.cmd instead.
    exit /b 2
)
call "%~dp0BUILD_ON_WINDOWS.cmd" --no-pause
if errorlevel 1 goto :fail_build
set "DOTNET_EXE="
if exist "%~dp0build-logs\selected_dotnet_path.txt" (
    set /p "DOTNET_EXE=" < "%~dp0build-logs\selected_dotnet_path.txt"
)
if not defined DOTNET_EXE goto :fail_sdk
echo [UI] Running actual Windows WPF smoke test and capturing screenshots...
"%DOTNET_EXE%" run --project "%~dp0source\tests\UiSmokeTests\UiSmokeTests.csproj" --configuration Release -- "%~dp0ui-check-results" > "%~dp0build-logs\05_ui_smoke.log" 2>&1
if errorlevel 1 goto :fail_ui
echo [SUCCESS] Windows UI test completed. Review ui-check-results.
pause
exit /b 0
:fail_build
echo [ERROR] Build failed. UI run was skipped.
pause
exit /b 1
:fail_sdk
echo [ERROR] Cannot find the SDK used by the successful build.
pause
exit /b 1
:fail_ui
echo [ERROR] UI smoke test failed. See build-logs\05_ui_smoke.log.
type "%~dp0build-logs\05_ui_smoke.log"
pause
exit /b 1
