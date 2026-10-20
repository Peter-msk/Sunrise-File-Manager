@echo off
setlocal EnableExtensions DisableDelayedExpansion
REM Diagnostic only: reads existing CurrentUser/My certificate metadata; no signing.
cd /d "%~dp0"
if errorlevel 1 exit /b 1
if /I "%~1"=="--group" set "SUNRISE_CERT_PROFILE=GROUP"
if /I "%~1"=="--capture" set "SUNRISE_CERT_PROFILE=CAPTURE"
echo [CHECK] Signing certificate preflight. No executable is signed or installed.
if not defined SUNRISE_CERT_PROFILE set "SUNRISE_CERT_PROFILE=GROUP"
echo [CHECK] Selected profile: %SUNRISE_CERT_PROFILE%
call "%CD%\build-tools\FindSunriseCertificate.cmd"
if errorlevel 1 goto :fail
echo [PASS] Certificate selected. Private key usability is checked by SignTool during build.
if /I not "%~2"=="--no-pause" pause
exit /b 0
:fail
echo [FAIL] Certificate discovery failed. Read build-logs\R14_signing_preflight.log.
echo [CHECK] Group CN and Capture CN are different identities.
if /I not "%~2"=="--no-pause" pause
exit /b 1
