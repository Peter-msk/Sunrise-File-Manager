@echo off
setlocal EnableExtensions DisableDelayedExpansion
REM R13 - unattended installer build with visible child stage and progress logs.
REM First signer generation is performed by the signed payload stage; no user prompts.
REM No tests, document fixtures or V3/security policy changes on company PC.
cd /d "%~dp0"
if errorlevel 1 exit /b 1
set "ROOT=%CD%"
echo [R13] Sunrise File Manager visible-stage signed installer build.
echo [R13] Prerequisites are checked first. The nested build output stays visible.
echo [R13][STEP 1/5] Checking toolchain.
call "%ROOT%\CHECK_INSTALLER_BUILD_ENV.cmd" --no-pause
if errorlevel 1 goto :fail_env
REM The inner build provisions or reuses the pinned signer automatically.
REM It never guesses among other CN=Sunrise Group certificates.
echo [R13][STEP 2/5] Building signed payload and installer.
call "%ROOT%\BUILD_RELEASE_INSTALLER.cmd" --company --no-pause
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" goto :fail_build
echo [SUCCESS] Signed installer is in dist\.
exit /b 0
:fail_env
echo [FAILED] Build prerequisites missing. See build-logs\INSTALLER_ENV_PREFLIGHT.txt.
exit /b 1
:fail_build
echo [FAILED] Installer build did not pass required gates.
if exist "%ROOT%\build-logs\R15_INSTALLER_BUILD_RESULT.txt" type "%ROOT%\build-logs\R15_INSTALLER_BUILD_RESULT.txt"
echo [DETAIL] See build-logs\R15_payload_build.log and R14_* diagnostics.
exit /b %RC%
