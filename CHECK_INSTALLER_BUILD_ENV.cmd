@echo off
setlocal EnableExtensions DisableDelayedExpansion
REM Read-only prerequisite check. Does not build, sign, install, open company documents.
cd /d "%~dp0"
if errorlevel 1 exit /b 1
set "ROOT=%CD%"
set "LOG=%ROOT%\build-logs"
if not exist "%LOG%" mkdir "%LOG%"
set "REPORT=%LOG%\INSTALLER_ENV_PREFLIGHT.txt"
> "%REPORT%" echo STATUS=CHECKING
set "MISSING=0"
echo [CHECK] Sunrise File Manager installer build prerequisites
echo [CHECK] No installer will run automatically. No V3 exception or security bypass.
if exist "%ROOT%\COMPANY_SOURCE_ONLY.flag" (echo [PASS] Company source policy marker) else (echo [FAIL] Company source marker missing & set /a MISSING+=1 >nul)
if exist "%ROOT%\installer\SunriseFileManager.iss" (echo [PASS] Inno source configuration) else (echo [FAIL] Inno source configuration missing & set /a MISSING+=1 >nul)
if exist "%ROOT%\installer\RELEASE_VERSION.txt" (echo [PASS] Installer release version file) else (echo [FAIL] Release version missing & set /a MISSING+=1 >nul)
if exist "%ROOT%\source\Sunrise.FileManager\Sunrise.FileManager.csproj" (echo [PASS] WPF project) else (echo [FAIL] WPF project missing & set /a MISSING+=1 >nul)
if exist "%ROOT%\build-tools\Sunrise.GroupSigning.ps1" (echo [PASS] Group-signing script) else (echo [FAIL] Group-signing script missing & set /a MISSING+=1 >nul)
if exist "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" (echo [PASS] Windows PowerShell executable) else (echo [FAIL] Windows PowerShell executable missing & set /a MISSING+=1 >nul)
set "SUNRISE_ISCC_RESOLVED="
call "%ROOT%\build-tools\FindInnoSetup.cmd" > "%LOG%\R15_inno_probe.log" 2>&1
if errorlevel 1 (
  echo [FAIL] Inno Setup 6/7 ISCC.exe missing or path invalid
  set /a MISSING+=1 >nul
) else (
  echo [PASS] Inno Setup ISCC.exe detected
  echo        "%SUNRISE_ISCC_RESOLVED%"
)
set "SUNRISE_SIGNTOOL_RESOLVED="
call "%ROOT%\build-tools\FindSignTool.cmd" > "%LOG%\R15_signtool_probe.log" 2>&1
if errorlevel 1 (
  echo [FAIL] Windows SDK SignTool missing or path invalid
  set /a MISSING+=1 >nul
) else (
  echo [PASS] Windows SDK SignTool detected
)
set "SDK10_FOUND="
for %%D in ("%ProgramW6432%\dotnet\dotnet.exe" "%ProgramFiles%\dotnet\dotnet.exe" "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" "%USERPROFILE%\.dotnet\dotnet.exe") do if not defined SDK10_FOUND if exist "%%~D" call :probe_sdk "%%~D"
for /f "delims=" %%D in ('where.exe dotnet.exe 2^>nul') do if not defined SDK10_FOUND call :probe_sdk "%%~D"
if defined SDK10_FOUND (echo [PASS] .NET 10 SDK detected) else (echo [FAIL] .NET 10 SDK not found & set /a MISSING+=1 >nul)
if "%MISSING%"=="0" (
  > "%REPORT%" echo STATUS=TOOLS_PRESENT
  >> "%REPORT%" echo WINDOWS_CERTIFICATE_AND_SIGNING=NOT_TESTED
  echo [READY] Required tools detected. Certificate and actual signing are checked during build.
  exit /b 0
)
> "%REPORT%" echo STATUS=MISSING_PREREQUISITES
>> "%REPORT%" echo COUNT=%MISSING%
>> "%REPORT%" echo WINDOWS_CERTIFICATE_AND_SIGNING=NOT_TESTED
echo [BLOCKED] %MISSING% prerequisite checks failed. See R15_inno_probe.log and R15_signtool_probe.log in build-logs.
echo [HELP] Inno Setup official download: https://jrsoftware.org/isdl.php
exit /b 1
:probe_sdk
for /f "tokens=1" %%S in ('"%~1" --list-sdks 2^>nul ^| findstr /R /C:"^10\."') do if not defined SDK10_FOUND set "SDK10_FOUND=%%S"
exit /b 0
