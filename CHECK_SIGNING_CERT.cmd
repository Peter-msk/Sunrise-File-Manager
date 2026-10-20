@echo off
setlocal EnableExtensions DisableDelayedExpansion
REM R07 read-only check. Build creates the self-signed certificate if no pin exists.
cd /d "%~dp0"
if errorlevel 1 exit /b 1
echo [CHECK] Sunrise Group signing. No certificate creation in this check.
call "%CD%\build-tools\EnsureSunriseGroupSigning.cmd" --check-only
if errorlevel 1 goto :fail
echo [READY] Signing prerequisites or project-pinned certificate verified.
if /I not "%~1"=="--no-pause" pause
exit /b 0
:fail
echo [FAIL] See build-logs\R14_cert_provision.log and R14_signing_preflight.log.
if /I not "%~1"=="--no-pause" pause
exit /b 1
