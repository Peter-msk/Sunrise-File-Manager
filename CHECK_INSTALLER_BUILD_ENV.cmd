@echo off
setlocal EnableExtensions DisableDelayedExpansion
REM Read-only prerequisite check. Does not build, sign, install, open company documents.
set "NO_PAUSE=0"
if /I "%~1"=="--no-pause" set "NO_PAUSE=1"
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
if exist "%ROOT%\build-tools\FindSunriseCertificate.cmd" (echo [PASS] Native certutil certificate discovery) else (echo [FAIL] Native certificate discovery missing & set /a MISSING+=1 >nul)
where.exe certutil.exe >nul 2>&1
if errorlevel 1 (echo [FAIL] Windows certutil.exe missing & set /a MISSING+=1 >nul) else (echo [PASS] Windows certutil.exe available)
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
REM Resolve the SDK once, without FOR /F command-substitution of quoted paths.
set "SUNRISE_DOTNET_RESOLVED="
set "SUNRISE_DOTNET_VERSION="
call "%ROOT%\build-tools\FindDotNet10.cmd" > "%LOG%\R15_dotnet_discovery.log" 2>&1
if errorlevel 1 (
  echo [FAIL] .NET 10 SDK missing, incompatible, or detection failed
  echo        Check build-logs\R15_dotnet_probe.log and R15_dotnet_candidate.log
  set /a MISSING+=1 >nul
) else (
  if not defined SUNRISE_DOTNET_RESOLVED (
    echo [FAIL] SDK resolver returned no executable
    set /a MISSING+=1 >nul
  ) else (
    echo [PASS] .NET 10 SDK detected: %SUNRISE_DOTNET_VERSION%
    echo        "%SUNRISE_DOTNET_RESOLVED%"
  )
)
if "%MISSING%"=="0" (
  > "%REPORT%" echo STATUS=TOOLS_PRESENT
  >> "%REPORT%" echo WINDOWS_CERTIFICATE_AND_SIGNING=NOT_TESTED
  echo [READY] Required tools detected. Certificate and actual signing are checked separately.
  echo [NEXT] Run CHECK_SIGNING_CERT.cmd before BUILD_ON_WINDOWS_COMPANY.cmd.
  if "%NO_PAUSE%"=="0" pause
  exit /b 0
)
> "%REPORT%" echo STATUS=MISSING_PREREQUISITES
>> "%REPORT%" echo COUNT=%MISSING%
>> "%REPORT%" echo WINDOWS_CERTIFICATE_AND_SIGNING=NOT_TESTED
echo [BLOCKED] %MISSING% prerequisite checks failed. See R15_inno_probe.log, R15_signtool_probe.log and R15_dotnet_probe.log in build-logs.
echo [HELP] Inno Setup official download: https://jrsoftware.org/isdl.php
if "%NO_PAUSE%"=="0" pause
exit /b 1
