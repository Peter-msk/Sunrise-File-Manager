@echo off
REM R15 OFFICIAL installer build: company-safe, Sunrise Group Authenticode-signed setup.
REM Never run LogicTests, document fixtures or preview tests on this company PC.
call "%~dp0BUILD_RELEASE_INSTALLER.cmd" --company --no-pause
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
 echo [BUILD FAILED] See build-logs\R15_INSTALLER_BUILD_RESULT.txt.
 if exist "%~dp0build-logs\R15_INSTALLER_BUILD_RESULT.txt" type "%~dp0build-logs\R15_INSTALLER_BUILD_RESULT.txt"
 echo [TIP] If VERSION_OR_INSTALLER_CONFIG, open build-logs\R15_version_preflight.log.
)
if "%RC%"=="0" echo [BUILD SUCCESS] Find your signed Setup EXE inside dist\.
pause
exit /b %RC%
