@echo off
setlocal EnableExtensions DisableDelayedExpansion
REM R22 - one-command official build. Static source checks are done before release; native gates remain.
REM First signer generation is performed by the signed payload stage; no user prompts.
REM No tests, document fixtures or V3/security policy changes on company PC.
cd /d "%~dp0"
if errorlevel 1 exit /b 1
set "ROOT=%CD%"
echo [BUILD] Sunrise File Manager 1.1.0 official installer.
REM Source-layout and toolchain inventory are validated before distributing this source ZIP.
REM The nested script still checks live SDK/Inno/SignTool availability and verifies signatures.
REM The inner build provisions or reuses the pinned signer automatically.
echo [BUILD] Compiling, signing and generating release metadata...
call "%ROOT%\BUILD_RELEASE_INSTALLER.cmd" --company --no-pause
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" goto :fail_build
echo [SUCCESS] Signed installer and release metadata are in dist\.
exit /b 0
:fail_build
echo [FAILED] Installer build did not pass required gates.
if exist "%ROOT%\build-logs\R15_INSTALLER_BUILD_RESULT.txt" type "%ROOT%\build-logs\R15_INSTALLER_BUILD_RESULT.txt"
echo [DETAIL] See build-logs\R15_payload_build.log and R14_* diagnostics.
exit /b %RC%
