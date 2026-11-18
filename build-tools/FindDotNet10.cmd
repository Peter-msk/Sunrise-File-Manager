@echo off
REM Shared .NET 10 SDK discovery for official Windows x64 installer and payload builds.
REM Read-only: does not install SDKs or change system settings. Caller must cd to source root.
REM Outputs SUNRISE_DOTNET_RESOLVED and SUNRISE_DOTNET_VERSION in caller environment.
set "SUNRISE_DOTNET_RESOLVED="
set "SUNRISE_DOTNET_VERSION="
set "SDK_PROBE_DIR=%~dp0..\build-logs"
if not exist "%SDK_PROBE_DIR%" mkdir "%SDK_PROBE_DIR%"
if not exist "%SDK_PROBE_DIR%" exit /b 1
set "SDK_PROBE_FILE=%SDK_PROBE_DIR%\R15_dotnet_candidate.log"
set "SDK_VERSION_FILE=%SDK_PROBE_DIR%\R15_dotnet_version_output.log"
set "SDK_MATCH_FILE=%SDK_PROBE_DIR%\R15_dotnet_matching_version.log"
set "SDK_TRACE=%SDK_PROBE_DIR%\R15_dotnet_probe.log"
> "%SDK_TRACE%" echo SDK_PROBE=START
> "%SDK_PROBE_FILE%" echo SDK_CANDIDATES=START
> "%SDK_MATCH_FILE%" type nul
REM Explicit override is authoritative. Accept either an exe or its installation directory.
if defined SUNRISE_DOTNET (
  call :root "%SUNRISE_DOTNET%"
  if defined SUNRISE_DOTNET_RESOLVED goto :success
  >> "%SDK_TRACE%" echo RESULT=INVALID_EXPLICIT_OVERRIDE
  echo [DOTNET] SUNRISE_DOTNET override cannot select .NET 10 under global.json.
  exit /b 1
)
REM Custom installation roots and x64 registered install locations, before default hosts.
if defined DOTNET_ROOT_X64 call :root "%DOTNET_ROOT_X64%"
if defined DOTNET_ROOT call :root "%DOTNET_ROOT%"
if defined DOTNET_INSTALL_DIR call :root "%DOTNET_INSTALL_DIR%"
if defined SUNRISE_DOTNET_RESOLVED goto :success
for /f "tokens=1,2,*" %%A in ('reg.exe query "HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64" /v InstallLocation /reg:64 2^>nul') do if /I "%%A"=="InstallLocation" call :root "%%C"
for /f "tokens=1,2,*" %%A in ('reg.exe query "HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64" /v InstallLocation /reg:32 2^>nul') do if /I "%%A"=="InstallLocation" call :root "%%C"
for /f "tokens=1,2,*" %%A in ('reg.exe query "HKCU\SOFTWARE\dotnet\Setup\InstalledVersions\x64" /v InstallLocation 2^>nul') do if /I "%%A"=="InstallLocation" call :root "%%C"
if defined SUNRISE_DOTNET_RESOLVED goto :success
REM Prefer absolute standard and per-user paths; also support x64 SDK on ARM64 Windows.
if defined ProgramW6432 call :root "%ProgramW6432%\dotnet\x64"
if defined ProgramW6432 call :root "%ProgramW6432%\dotnet"
if defined ProgramFiles call :root "%ProgramFiles%\dotnet\x64"
if defined ProgramFiles call :root "%ProgramFiles%\dotnet"
if defined LOCALAPPDATA call :root "%LOCALAPPDATA%\Microsoft\dotnet"
if defined LOCALAPPDATA call :root "%LOCALAPPDATA%\dotnet"
if defined USERPROFILE call :root "%USERPROFILE%\.dotnet"
if defined SUNRISE_DOTNET_RESOLVED goto :success
for /f "delims=" %%D in ('where.exe dotnet.exe 2^>nul') do call :candidate "%%~D"
if defined SUNRISE_DOTNET_RESOLVED goto :success
>> "%SDK_TRACE%" echo RESULT=NOT_FOUND_OR_NO_COMPATIBLE_SDK
 echo [DOTNET] No discovered host could select a compatible .NET 10 SDK. An SDK may exist at another location.
 echo [DOTNET] See build-logs\R15_dotnet_probe.log and R15_dotnet_candidate.log for each host and its visible SDKs.
 echo [DOTNET] Run SDK_DIAGNOSTICS.cmd or set SUNRISE_DOTNET to the actual approved dotnet.exe path.
exit /b 1
:success
>> "%SDK_TRACE%" echo RESULT=PASS
>> "%SDK_TRACE%" echo SDK=%SUNRISE_DOTNET_VERSION%
 echo [DOTNET] Found .NET %SUNRISE_DOTNET_VERSION%: "%SUNRISE_DOTNET_RESOLVED%"
exit /b 0
:root
if defined SUNRISE_DOTNET_RESOLVED exit /b 0
if "%~1"=="" exit /b 0
if /I "%~x1"==".exe" (
  call :candidate "%~1"
) else (
  call :candidate "%~1\dotnet.exe"
)
exit /b 0
:candidate
if defined SUNRISE_DOTNET_RESOLVED exit /b 0
if not exist "%~1" exit /b 0
REM Avoid re-probing the same absolute host from multiple environment variables or PATH.
for /f "usebackq tokens=1,* delims==" %%A in ("%SDK_TRACE%") do if /I "%%A"=="CHECK" if /I "%%~B"=="%~f1" exit /b 0
>> "%SDK_TRACE%" echo CHECK="%~f1"
>> "%SDK_PROBE_FILE%" echo ===== HOST: "%~f1" =====
REM Direct invocation preserves quoting and source global.json SDK selection.
"%~f1" --version > "%SDK_VERSION_FILE%" 2>&1
set "SDK_CANDIDATE_RC=%ERRORLEVEL%"
>> "%SDK_PROBE_FILE%" echo VERSION_EXIT_CODE=%SDK_CANDIDATE_RC%
>> "%SDK_PROBE_FILE%" type "%SDK_VERSION_FILE%"
if not "%SDK_CANDIDATE_RC%"=="0" goto :candidate_failed
REM Require exactly a stable 10.0 SDK version, not an error message or preview.
findstr /R /X /C:"10\.0\.[0-9][0-9]*" "%SDK_VERSION_FILE%" > "%SDK_MATCH_FILE%" 2>nul
if errorlevel 1 goto :candidate_failed
for /f "usebackq tokens=1" %%V in ("%SDK_MATCH_FILE%") do if not defined SUNRISE_DOTNET_RESOLVED (
  set "SUNRISE_DOTNET_RESOLVED=%~f1"
  set "SUNRISE_DOTNET_VERSION=%%V"
)
if not defined SUNRISE_DOTNET_RESOLVED goto :candidate_failed
>> "%SDK_TRACE%" echo CHECK_RESULT=PASS
exit /b 0
:candidate_failed
>> "%SDK_TRACE%" echo CHECK_RESULT=NO_DOTNET_10_SELECTED EXIT_CODE=%SDK_CANDIDATE_RC%
>> "%SDK_PROBE_FILE%" echo INSTALLED_SDKS_FOR_THIS_HOST:
"%~f1" --list-sdks >> "%SDK_PROBE_FILE%" 2>&1
>> "%SDK_PROBE_FILE%" echo.
exit /b 0
