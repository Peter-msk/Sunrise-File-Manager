@echo off
REM Inno Setup ISCC.exe location resolver. Called from official company build scripts.
REM Does NOT install/download/execute software, modify PATH, or change security policy.
REM Output: SUNRISE_ISCC_RESOLVED, set only for an existing ISCC.exe.
set "SUNRISE_ISCC_RESOLVED="
REM An explicit override must be correct; never fall through to another executable.
if defined SUNRISE_ISCC (
  call :candidate "%SUNRISE_ISCC%"
  if defined SUNRISE_ISCC_RESOLVED goto :success
  echo [INNO] Invalid SUNRISE_ISCC path. It must point to an existing ISCC.exe.
  exit /b 1
)
REM Prefer existing PATH tool before documented installation locations.
for /f "delims=" %%I in ('where.exe ISCC.exe 2^>nul') do if not defined SUNRISE_ISCC_RESOLVED call :candidate "%%~I"
if defined SUNRISE_ISCC_RESOLVED goto :success
REM Per-user + per-machine default install locations (both Inno Setup 6 and 7).
for %%V in (6 7) do (
  if not defined SUNRISE_ISCC_RESOLVED call :candidate "%ProgramFiles%\Inno Setup %%V\ISCC.exe"
  if not defined SUNRISE_ISCC_RESOLVED call :candidate "%ProgramFiles(x86)%\Inno Setup %%V\ISCC.exe"
  if not defined SUNRISE_ISCC_RESOLVED call :candidate "%LOCALAPPDATA%\Programs\Inno Setup %%V\ISCC.exe"
  if not defined SUNRISE_ISCC_RESOLVED call :candidate "%LOCALAPPDATA%\Inno Setup %%V\ISCC.exe"
)
if defined SUNRISE_ISCC_RESOLVED goto :success
REM Inno install path in common Windows uninstall registry keys (custom installs).
for %%V in (6 7) do (
  if not defined SUNRISE_ISCC_RESOLVED call :registry "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup %%V_is1"
  if not defined SUNRISE_ISCC_RESOLVED call :registry "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup %%V_is1"
  if not defined SUNRISE_ISCC_RESOLVED call :registry "HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup %%V_is1"
)
if defined SUNRISE_ISCC_RESOLVED goto :success
echo [INNO] ISCC.exe not found in PATH, common install directories or Windows uninstall registry.
echo [INNO] If already installed elsewhere, set SUNRISE_ISCC to the absolute path of ISCC.exe.
echo [INNO] If not installed, obtain Inno Setup from https://jrsoftware.org/isdl.php with IT approval.
exit /b 1
:success
echo [INNO] Found ISCC.exe: "%SUNRISE_ISCC_RESOLVED%"
exit /b 0
:candidate
if not exist "%~1" exit /b 0
for %%I in ("%~1") do if /I "%%~nxI"=="ISCC.exe" set "SUNRISE_ISCC_RESOLVED=%%~fI"
exit /b 0
:registry
for /f "tokens=1,2,*" %%A in ('reg.exe query "%~1" /v InstallLocation 2^>nul ^| findstr /I /C:"REG_SZ" /C:"REG_EXPAND_SZ"') do if not defined SUNRISE_ISCC_RESOLVED call :candidate "%%C\ISCC.exe"
exit /b 0
