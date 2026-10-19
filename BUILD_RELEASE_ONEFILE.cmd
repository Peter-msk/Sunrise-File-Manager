@echo off
setlocal EnableExtensions DisableDelayedExpansion
REM R10: Source-free Windows x64 self-contained SINGLE-EXE release.
REM Company policy: no LogicTests, fixture/document scans, or UI capture.
REM The user's source tree is NEVER copied into the end-user release.
cd /d "%~dp0"
set "ROOT=%CD%"
set "DIST=%ROOT%\dist"
set "LOG_DIR=%ROOT%\build-logs"
set "PROJECT=%ROOT%\source\Sunrise.FileManager\Sunrise.FileManager.csproj"
set "PUBLISH_DIR=%DIST%\_R10_onefile_publish"
set "ZIP_DIR=%DIST%\_R10_onefile_zip_stage"
set "STAGE_EXE=%DIST%\_R10_Sunrise_File_Manager_staging.exe"
set "FINAL_EXE=%DIST%\Sunrise File Manager.exe"
set "STAGE_ZIP=%DIST%\_R10_Sunrise_File_Manager_staging.zip"
set "FINAL_ZIP=%DIST%\Sunrise_File_Manager_OneFile_Windows_x64_R10.zip"
set "BACKUP_EXE=%DIST%\_R10_previous.exe"
set "BACKUP_ZIP=%DIST%\_R10_previous.zip"
set "RESULT=%LOG_DIR%\R10_RELEASE_RESULT.txt"
set "NO_PAUSE=0"
if /I "%~1"=="--no-pause" set "NO_PAUSE=1"
if /I "%~2"=="--no-pause" set "NO_PAUSE=1"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
set "DOTNET_CLI_UI_LANGUAGE=en"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"
if not exist "%DIST%" mkdir "%DIST%"
> "%RESULT%" echo STATUS=RUNNING
>> "%RESULT%" echo STAGE=PREFLIGHT
>> "%RESULT%" echo MODE=COMPANY_NO_DOCUMENT_TESTS
if not exist "%PROJECT%" goto :fail_source
if not exist "%ROOT%\COMPANY_SOURCE_ONLY.flag" goto :fail_policy

set "DOTNET_EXE="
set "FOUND_SDK_VER="
for %%D in (
    "%ProgramW6432%\dotnet\dotnet.exe"
    "%ProgramFiles%\dotnet\dotnet.exe"
    "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
    "%USERPROFILE%\.dotnet\dotnet.exe"
) do if not defined DOTNET_EXE if exist "%%~D" call :probe_sdk "%%~D"
for /f "delims=" %%D in ('where.exe dotnet.exe 2^>nul') do if not defined DOTNET_EXE call :probe_sdk "%%~D"
if not defined DOTNET_EXE goto :fail_sdk
for %%I in ("%DOTNET_EXE%") do set "DOTNET_ROOT=%%~dpI"
if "%DOTNET_ROOT:~-1%"=="\" set "DOTNET_ROOT=%DOTNET_ROOT:~0,-1%"
set "PATH=%DOTNET_ROOT%;%PATH%"
echo ============================================================================
echo  Sunrise File Manager - R10 Windows x64 ONE-FILE Release
echo  SDK: %FOUND_SDK_VER% / WPF net10.0 / Company no-document-test policy
echo ============================================================================
echo [INFO] Ollama installation is NOT required for building.
echo [INFO] Self-contained ONE EXE. No development source included.
echo [INFO] Some .NET native components may extract at startup.
echo.

if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%ZIP_DIR%" rmdir /s /q "%ZIP_DIR%"
if exist "%STAGE_EXE%" del /f /q "%STAGE_EXE%"
if exist "%STAGE_ZIP%" del /f /q "%STAGE_ZIP%"
mkdir "%PUBLISH_DIR%"
if errorlevel 1 goto :fail_publish
> "%RESULT%" echo STATUS=RUNNING
>> "%RESULT%" echo STAGE=ONEFILE_PUBLISH
>> "%RESULT%" echo LOGIC_TESTS=SKIPPED_COMPANY_POLICY
echo [1/3] Publishing WPF as a self-contained single EXE...
echo [COMPANY POLICY] No LogicTests, PDF/HWPX fixture scans or UI capture.
"%DOTNET_EXE%" publish "%PROJECT%" --configuration Release --framework net10.0-windows10.0.19041.0 --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:DebugType=none -p:DebugSymbols=false -p:SunriseCompanyBuild=1 --output "%PUBLISH_DIR%" > "%LOG_DIR%\R10_01_singlefile_publish.log" 2>&1
if errorlevel 1 goto :fail_publish
if not exist "%PUBLISH_DIR%\Sunrise.FileManager.exe" goto :fail_publish
REM A distributable ONEFILE must not require adjacent runtime DLL/JSON files.
for %%F in ("%PUBLISH_DIR%\*.dll" "%PUBLISH_DIR%\*.json" "%PUBLISH_DIR%\*.pdb") do if exist "%%~F" goto :fail_extra_payload
for /f "delims=" %%F in ('dir /b /a:-d "%PUBLISH_DIR%" 2^>nul') do if /I not "%%F"=="Sunrise.FileManager.exe" goto :fail_extra_payload
for /f "delims=" %%D in ('dir /b /a:d "%PUBLISH_DIR%" 2^>nul') do goto :fail_extra_payload
for %%F in ("%PUBLISH_DIR%\Sunrise.FileManager.exe") do if %%~zF LSS 1000000 goto :fail_publish
copy /b /y "%PUBLISH_DIR%\Sunrise.FileManager.exe" "%STAGE_EXE%" >nul
if errorlevel 1 goto :fail_publish

> "%RESULT%" echo STATUS=RUNNING
>> "%RESULT%" echo STAGE=ONEFILE_PACKAGE
>> "%RESULT%" echo LOGIC_TESTS=SKIPPED_COMPANY_POLICY
echo [2/3] Preparing EXE-only release and separate legal notices...
mkdir "%ZIP_DIR%"
if errorlevel 1 goto :fail_package
copy /b /y "%STAGE_EXE%" "%ZIP_DIR%\Sunrise File Manager.exe" >nul
if errorlevel 1 goto :fail_package
REM License notices are in an isolated legal folder, never mixed with the EXE.
mkdir "%ZIP_DIR%\_licenses"
if errorlevel 1 goto :fail_package
copy /y "%ROOT%\LICENSE" "%ZIP_DIR%\_licenses\LICENSE" >nul
if errorlevel 1 goto :fail_package
if exist "%ROOT%\source\licenses" (
    robocopy "%ROOT%\source\licenses" "%ZIP_DIR%\_licenses" /E /NFL /NDL /NJH /NJS /NP > "%LOG_DIR%\R10_02_licenses_copy.log" 2>&1
    if errorlevel 8 goto :fail_package
)
where.exe tar.exe >nul 2>&1
if errorlevel 1 goto :fail_tar
tar.exe -a -c -f "%STAGE_ZIP%" -C "%ZIP_DIR%" . > "%LOG_DIR%\R10_03_release_zip.log" 2>&1
if errorlevel 1 goto :fail_package
if not exist "%STAGE_ZIP%" goto :fail_package
tar.exe -t -f "%STAGE_ZIP%" > "%LOG_DIR%\R10_04_release_contents.log" 2>&1
if errorlevel 1 goto :fail_package
findstr /I /C:"Sunrise File Manager.exe" "%LOG_DIR%\R10_04_release_contents.log" >nul
if errorlevel 1 goto :fail_package
REM Do not distribute any C# sources, XAML, fixtures or embedded office documents.
findstr /I /R /C:"\.cs$" /C:"\.xaml$" /C:"\.pdf$" /C:"\.hwpx$" /C:"\.docx$" /C:"Fixtures/" /C:"source/" "%LOG_DIR%\R10_04_release_contents.log" >nul
if not errorlevel 1 goto :fail_package

> "%RESULT%" echo STATUS=RUNNING
>> "%RESULT%" echo STAGE=COMMIT
echo [3/3] Verifying and installing the release files...
if exist "%BACKUP_EXE%" del /f /q "%BACKUP_EXE%"
if exist "%BACKUP_ZIP%" del /f /q "%BACKUP_ZIP%"
if exist "%FINAL_EXE%" (
 move /y "%FINAL_EXE%" "%BACKUP_EXE%" >nul
 if errorlevel 1 goto :fail_preserve
)
if exist "%FINAL_ZIP%" (
 move /y "%FINAL_ZIP%" "%BACKUP_ZIP%" >nul
 if errorlevel 1 goto :fail_preserve
)
move /y "%STAGE_EXE%" "%FINAL_EXE%" >nul
if errorlevel 1 goto :fail_commit
move /y "%STAGE_ZIP%" "%FINAL_ZIP%" >nul
if errorlevel 1 goto :fail_commit
if not exist "%FINAL_EXE%" goto :fail_commit
if not exist "%FINAL_ZIP%" goto :fail_commit
if exist "%BACKUP_EXE%" del /f /q "%BACKUP_EXE%"
if exist "%BACKUP_ZIP%" del /f /q "%BACKUP_ZIP%"
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%ZIP_DIR%" rmdir /s /q "%ZIP_DIR%"
> "%RESULT%" echo STATUS=SUCCESS
>> "%RESULT%" echo STAGE=COMPLETE
>> "%RESULT%" echo MODE=COMPANY_NO_DOCUMENT_TESTS
>> "%RESULT%" echo FORMAT=SELF_CONTAINED_SINGLE_FILE
>> "%RESULT%" echo LOGIC_TESTS=SKIPPED_COMPANY_POLICY
>> "%RESULT%" echo SDK=%FOUND_SDK_VER%
>> "%RESULT%" echo EXE=%FINAL_EXE%
>> "%RESULT%" echo ZIP=%FINAL_ZIP%
echo ============================================================================
echo [SUCCESS] ONE-FILE RELEASE READY
 echo  EXE : "%FINAL_EXE%"
 echo  ZIP : "%FINAL_ZIP%"
echo ============================================================================
REM Open Explorer with the EXE selected; never auto-run the app on company PC.
if "%NO_PAUSE%"=="0" start "" explorer.exe /select,"%FINAL_EXE%"
if "%NO_PAUSE%"=="0" pause
exit /b 0

:probe_sdk
set "SDK_CANDIDATE=%~1"
"%SDK_CANDIDATE%" --list-sdks > "%LOG_DIR%\R10_sdk_probe.log" 2>&1
if errorlevel 1 exit /b 0
set "SDK_MATCH="
for /f "tokens=1" %%V in ('findstr /R /B /C:"10\." "%LOG_DIR%\R10_sdk_probe.log" 2^>nul') do if not defined SDK_MATCH set "SDK_MATCH=%%V"
if not defined SDK_MATCH exit /b 0
set "DOTNET_EXE=%SDK_CANDIDATE%"
set "FOUND_SDK_VER=%SDK_MATCH%"
exit /b 0

:fail_source
echo [ERROR] Project source is missing.
set "FAILED_STAGE=PREFLIGHT_SOURCE"
goto :fail_common
:fail_policy
echo [ERROR] Company security marker is missing. Refusing company release.
set "FAILED_STAGE=COMPANY_POLICY"
goto :fail_common
:fail_sdk
echo [ERROR] Compatible .NET 10 SDK not found.
set "FAILED_STAGE=PREFLIGHT_SDK"
goto :fail_common
:fail_publish
echo [ERROR] One-file WPF publish failed. See build-logs\R10_01_singlefile_publish.log.
if exist "%LOG_DIR%\R10_01_singlefile_publish.log" type "%LOG_DIR%\R10_01_singlefile_publish.log"
set "FAILED_STAGE=ONEFILE_PUBLISH"
goto :fail_common
:fail_extra_payload
echo [ERROR] Publish left runtime DLL, JSON, or debug files outside the EXE. Not a one-file release.
set "FAILED_STAGE=ONEFILE_EXTRA_DEPENDENCIES"
goto :fail_common
:fail_tar
echo [ERROR] tar.exe required for the optional release ZIP is unavailable.
set "FAILED_STAGE=ZIP_TOOL"
goto :fail_common
:fail_package
echo [ERROR] One-file ZIP packaging failed. See build-logs\R10_03_release_zip.log.
set "FAILED_STAGE=ONEFILE_PACKAGE"
goto :fail_common
:fail_preserve
echo [ERROR] Could not safely back up previous release. No prior files will be deleted.
if exist "%BACKUP_EXE%" if not exist "%FINAL_EXE%" move /y "%BACKUP_EXE%" "%FINAL_EXE%" >nul
if exist "%BACKUP_ZIP%" if not exist "%FINAL_ZIP%" move /y "%BACKUP_ZIP%" "%FINAL_ZIP%" >nul
set "FAILED_STAGE=ONEFILE_PRESERVE"
goto :fail_common
:fail_commit
echo [ERROR] Failed to commit release. Restoring last successful EXE/ZIP.
if exist "%FINAL_EXE%" del /f /q "%FINAL_EXE%"
if exist "%FINAL_ZIP%" del /f /q "%FINAL_ZIP%"
if exist "%BACKUP_EXE%" move /y "%BACKUP_EXE%" "%FINAL_EXE%" >nul
if exist "%BACKUP_ZIP%" move /y "%BACKUP_ZIP%" "%FINAL_ZIP%" >nul
set "FAILED_STAGE=ONEFILE_COMMIT"
goto :fail_common
:fail_common
> "%RESULT%" echo STATUS=FAILED
>> "%RESULT%" echo STAGE=%FAILED_STAGE%
>> "%RESULT%" echo MODE=COMPANY_NO_DOCUMENT_TESTS
>> "%RESULT%" echo SDK=%FOUND_SDK_VER%
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%ZIP_DIR%" rmdir /s /q "%ZIP_DIR%"
if exist "%STAGE_EXE%" del /f /q "%STAGE_EXE%"
if exist "%STAGE_ZIP%" del /f /q "%STAGE_ZIP%"
echo [BUILD FAILED] Stage: %FAILED_STAGE%
echo [INFO] Inspect "%RESULT%" and build-logs\R10_01_singlefile_publish.log.
if "%NO_PAUSE%"=="0" pause
exit /b 1
