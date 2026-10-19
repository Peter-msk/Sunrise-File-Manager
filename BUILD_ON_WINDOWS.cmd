@echo off
REM R15 OFFICIAL installable distribution. Use same policy as company entry point.
call "%~dp0BUILD_ON_WINDOWS_COMPANY.cmd"
exit /b %ERRORLEVEL%
