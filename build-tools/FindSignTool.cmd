@echo off
REM Windows SDK SignTool resolver, no tool execution/installation.
REM Returns SUNRISE_SIGNTOOL_RESOLVED for official build scripts.
set "SUNRISE_SIGNTOOL_RESOLVED="
if defined SUNRISE_SIGNTOOL (
  call :candidate "%SUNRISE_SIGNTOOL%"
  if defined SUNRISE_SIGNTOOL_RESOLVED exit /b 0
  echo [SIGNTOOL] Invalid SUNRISE_SIGNTOOL path: expected an existing signtool.exe.
  exit /b 1
)
for /f "delims=" %%S in ('where.exe signtool.exe 2^>nul') do if not defined SUNRISE_SIGNTOOL_RESOLVED call :candidate "%%~S"
if defined SUNRISE_SIGNTOOL_RESOLVED goto :success
for /d %%V in ("%ProgramFiles(x86)%\Windows Kits\10\bin\10.*") do if not defined SUNRISE_SIGNTOOL_RESOLVED call :candidate "%%~V\x64\signtool.exe"
for /d %%V in ("%ProgramFiles%\Windows Kits\10\bin\10.*") do if not defined SUNRISE_SIGNTOOL_RESOLVED call :candidate "%%~V\x64\signtool.exe"
if defined SUNRISE_SIGNTOOL_RESOLVED goto :success
echo [SIGNTOOL] Windows SDK SignTool not found. Check Windows SDK installation.
echo [SIGNTOOL] For an approved custom path, set SUNRISE_SIGNTOOL=C:\Path\To\signtool.exe.
exit /b 1
:success
echo [SIGNTOOL] Found: "%SUNRISE_SIGNTOOL_RESOLVED%"
exit /b 0
:candidate
if not exist "%~1" exit /b 0
for %%I in ("%~1") do if /I "%%~nxI"=="signtool.exe" set "SUNRISE_SIGNTOOL_RESOLVED=%%~fI"
exit /b 0
