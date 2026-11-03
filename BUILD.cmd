@echo off
setlocal EnableExtensions DisableDelayedExpansion
REM Sunrise File Manager - single supported Windows build entry point. Version from installer/RELEASE_VERSION.txt.
REM Double click BUILD.cmd = official self-contained, signed Setup + signed preview.
REM The internal scripts are not individual user entry points.
cd /d "%~dp0"
if errorlevel 1 exit /b 1
set "ROOT=%CD%"
set "ACTION=release"
set "NO_PAUSE=0"
if /I "%~1"=="--dev" set "ACTION=dev"
if /I "%~1"=="--preview" set "ACTION=preview"
if /I "%~1"=="--check" set "ACTION=check"
if /I "%~1"=="--splits" set "ACTION=splits"
if /I "%~1"=="--release" set "ACTION=release"
if /I "%~1"=="--help" goto :usage_ok
if /I "%~1"=="--no-pause" set "NO_PAUSE=1"
if not "%~1"=="" if /I not "%~1"=="--release" if /I not "%~1"=="--dev" if /I not "%~1"=="--preview" if /I not "%~1"=="--check" if /I not "%~1"=="--splits" if /I not "%~1"=="--no-pause" goto :usage_error
if /I "%~2"=="--no-pause" set "NO_PAUSE=1"
if not "%~2"=="" if /I not "%~2"=="--no-pause" goto :usage_error
if not "%~3"=="" goto :usage_error
set "INNER=%ROOT%\build-tools\internal"
if /I "%ACTION%"=="release" (
  REM Default release must remain signed, fully bundled and policy-gated.
  call "%INNER%\BUILD_ON_WINDOWS_COMPANY.cmd"
) else if /I "%ACTION%"=="dev" (
  call "%INNER%\BUILD_DEV_EXE.cmd" --no-pause
) else if /I "%ACTION%"=="preview" (
  call "%INNER%\BUILD_PREVIEW_ON_WINDOWS.cmd"
) else if /I "%ACTION%"=="check" (
  call "%INNER%\CHECK_INSTALLER_BUILD_ENV.cmd" --no-pause
) else if /I "%ACTION%"=="splits" (
  call "%INNER%\CHECK_SPLITS_ON_WINDOWS.cmd" --no-pause
)
set "RC=%ERRORLEVEL%"
echo.
if "%RC%"=="0" (
  echo [PASS] Sunrise File Manager build mode %ACTION%.
  if /I "%ACTION%"=="release" (
    set "SFM_PRINT_VERSION="
    for /f "usebackq delims=" %%V in ("%ROOT%\installer\RELEASE_VERSION.txt") do if not defined SFM_PRINT_VERSION set "SFM_PRINT_VERSION=%%V"
    call echo [OUTPUT] dist\Sunrise_File_Manager_%%SFM_PRINT_VERSION%%_Setup.exe
  )
) else (
  echo [FAILED] Build mode %ACTION% returned %RC%.
  echo [LOG] See build-logs\R15_INSTALLER_BUILD_RESULT.txt and build-logs\R14_wpf_publish.log.
)
if "%NO_PAUSE%"=="0" pause
exit /b %RC%
:usage_ok
echo Usage: BUILD.cmd [--release ^| --dev ^| --preview ^| --check ^| --splits] [--no-pause]
echo Default: signed official Setup and signed Preview.
exit /b 0
:usage_error
echo [ERROR] Unsupported option.
:usage
echo Usage: BUILD.cmd [--release ^| --dev ^| --preview ^| --check ^| --splits] [--no-pause]
echo   default     Signed production Setup + signed install-free Preview.
echo   --dev       Unsigned development-only EXE; .NET SDK required.
echo   --preview   Signed install-free Preview; no Inno Setup.
echo   --check     Read-only build prerequisites.
echo   --splits    Manual 1-4 split WPF UI test, not part of normal build.
echo   --no-pause  For CI/automation; all result codes are propagated.
exit /b 2
