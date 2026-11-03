@echo off
setlocal EnableExtensions DisableDelayedExpansion
REM One-click signed install-free preview. Does NOT compile a Setup or run Inno Setup.
REM Reuse the normal signed Windows build; no duplicated source tests are run.
cd /d "%~dp0..\.."
if errorlevel 1 exit /b 1
set "ROOT=%CD%"
set "SUNRISE_LIGHTWEIGHT=0"
set "SUNRISE_RELEASE_VERSION="
for /f "usebackq delims=" %%V in ("%ROOT%\installer\RELEASE_VERSION.txt") do if not defined SUNRISE_RELEASE_VERSION set "SUNRISE_RELEASE_VERSION=%%V"
if not defined SUNRISE_RELEASE_VERSION goto :fail
REM Require installer-owned version to match the actual app metadata before signing.
findstr /L /C:"<Version>%SUNRISE_RELEASE_VERSION%</Version>" "%ROOT%\source\Sunrise.FileManager\Sunrise.FileManager.csproj" >nul
if errorlevel 1 goto :fail
if not exist "%ROOT%\COMPANY_SOURCE_ONLY.flag" goto :fail
if not exist "%ROOT%\build-tools\PackageSignedPreview.cmd" goto :fail
echo [PREVIEW] Building and signing self-contained Sunrise File Manager %SUNRISE_RELEASE_VERSION%.
echo [PREVIEW] Inno Setup, installation, document inspections and test fixtures are not needed.
call "%ROOT%\build-tools\internal\BUILD_RELEASE_ONEDIR.cmd" --company --no-pause --installer-payload
if errorlevel 1 goto :fail
echo [PREVIEW] Packaging already signed app for direct EXE launch.
call "%ROOT%\build-tools\PackageSignedPreview.cmd" "%ROOT%\dist\_R18_installer_payload\app" "%SUNRISE_RELEASE_VERSION%"
if errorlevel 1 goto :fail
if exist "%ROOT%\dist\_R18_installer_payload" rmdir /s /q "%ROOT%\dist\_R18_installer_payload"
echo [SUCCESS] No installation required: dist\Sunrise_File_Manager_%SUNRISE_RELEASE_VERSION%_Preview\Sunrise.FileManager.exe
exit /b 0
:fail
echo [FAILED] Install-free preview was not completed. Check build-logs\R14_* and R17_*.
exit /b 1
