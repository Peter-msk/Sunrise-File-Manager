@echo off
REM Shared .NET 10 SDK discovery for official Windows x64 installer and payload builds.
REM Read-only: does not install SDKs, build user documents, or modify security settings.
REM The caller must cd to the source root so global.json governs SDK selection.
REM Outputs SUNRISE_DOTNET_RESOLVED and SUNRISE_DOTNET_VERSION in the caller environment.
set "SUNRISE_DOTNET_RESOLVED="
set "SUNRISE_DOTNET_VERSION="
set "SDK_PROBE_DIR=%~dp0..\build-logs"
if not exist "%SDK_PROBE_DIR%" mkdir "%SDK_PROBE_DIR%"
if not exist "%SDK_PROBE_DIR%" exit /b 1
set "SDK_PROBE_FILE=%SDK_PROBE_DIR%\R15_dotnet_candidate.log"
set "SDK_MATCH_FILE=%SDK_PROBE_DIR%\R15_dotnet_matching_version.log"
set "SDK_TRACE=%SDK_PROBE_DIR%\R15_dotnet_probe.log"
> "%SDK_TRACE%" echo SDK_PROBE=START
if defined SUNRISE_DOTNET (
  call :candidate "%SUNRISE_DOTNET%"
  if defined SUNRISE_DOTNET_RESOLVED goto :success
  >> "%SDK_TRACE%" echo RESULT=INVALID_EXPLICIT_OVERRIDE
  echo [DOTNET] SUNRISE_DOTNET override is missing or cannot select .NET 10 under global.json.
  exit /b 1
)
REM Per-machine and per-user installations. All candidate invocations quote their paths.
if defined ProgramW6432 if not defined SUNRISE_DOTNET_RESOLVED call :candidate "%ProgramW6432%\dotnet\dotnet.exe"
if defined ProgramFiles if not defined SUNRISE_DOTNET_RESOLVED call :candidate "%ProgramFiles%\dotnet\dotnet.exe"
if defined LOCALAPPDATA if not defined SUNRISE_DOTNET_RESOLVED call :candidate "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
if defined USERPROFILE if not defined SUNRISE_DOTNET_RESOLVED call :candidate "%USERPROFILE%\.dotnet\dotnet.exe"
if defined SUNRISE_DOTNET_RESOLVED goto :success
for /f "delims=" %%D in ('where.exe dotnet.exe 2^>nul') do if not defined SUNRISE_DOTNET_RESOLVED call :candidate "%%~D"
if defined SUNRISE_DOTNET_RESOLVED goto :success
>> "%SDK_TRACE%" echo RESULT=NOT_FOUND_OR_NO_COMPATIBLE_SDK
 echo [DOTNET] .NET 10 SDK is missing, unusable, or incompatible with global.json.
 echo [DOTNET] See build-logs\R15_dotnet_probe.log and R15_dotnet_candidate.log.
 echo [DOTNET] You may set SUNRISE_DOTNET to an approved dotnet.exe path.
exit /b 1
:success
>> "%SDK_TRACE%" echo RESULT=PASS
>> "%SDK_TRACE%" echo SDK=%SUNRISE_DOTNET_VERSION%
 echo [DOTNET] Found .NET %SUNRISE_DOTNET_VERSION%: "%SUNRISE_DOTNET_RESOLVED%"
exit /b 0
:candidate
if not exist "%~1" exit /b 0
>> "%SDK_TRACE%" echo CHECK=%~1
REM Direct invocation avoids FOR /F's nested cmd /c quote stripping on C:\Program Files.
"%~1" --version > "%SDK_PROBE_FILE%" 2>&1
if errorlevel 1 (
  >> "%SDK_TRACE%" echo CHECK_RESULT=DOTNET_VERSION_FAILED
  exit /b 0
)
REM Parse a captured file, not an embedded command; honors the source global.json.
findstr /R /B /C:"10\." "%SDK_PROBE_FILE%" > "%SDK_MATCH_FILE%" 2>nul
if errorlevel 1 (
  >> "%SDK_TRACE%" echo CHECK_RESULT=NO_DOTNET_10_SELECTED
  exit /b 0
)
for /f "usebackq tokens=1" %%V in ("%SDK_MATCH_FILE%") do if not defined SUNRISE_DOTNET_RESOLVED (
  set "SUNRISE_DOTNET_RESOLVED=%~1"
  set "SUNRISE_DOTNET_VERSION=%%V"
)
exit /b 0
