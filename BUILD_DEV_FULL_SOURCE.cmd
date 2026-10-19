@echo off
setlocal EnableExtensions

REM Sunrise File Manager: Windows x64 / .NET 10 / CMD-only Portable build
REM Does not install SDK, Ollama, or other software.
set "NO_PAUSE=0"
set "COMPANY_MODE=0"
if /I "%~1"=="--no-pause" set "NO_PAUSE=1"
if /I "%~2"=="--no-pause" set "NO_PAUSE=1"
if /I "%~1"=="--company" set "COMPANY_MODE=1"
if /I "%~2"=="--company" set "COMPANY_MODE=1"
REM A company-only source archive carries a marker to prevent accidental document tests.
if exist "%~dp0COMPANY_SOURCE_ONLY.flag" set "COMPANY_MODE=1"
cd /d "%~dp0"
set "ROOT=%CD%"
set "LOG_DIR=%ROOT%\build-logs"
set "DIST_DIR=%ROOT%\dist"
set "STAGE_DIR=%DIST_DIR%\_SunriseFileManager_R02_staging"
set "STAGE_ZIP=%DIST_DIR%\_SunriseFileManager_R02_staging.zip"
set "FINAL_DIR=%DIST_DIR%\SunriseFileManager_Portable_R02"
set "FINAL_ZIP=%DIST_DIR%\SunriseFileManager_Windows_x64_Portable_FullSource_R02.zip"
if "%COMPANY_MODE%"=="1" (
    set "STAGE_DIR=%DIST_DIR%\_SunriseFileManager_COMPANY_R04_staging"
    set "STAGE_ZIP=%DIST_DIR%\_SunriseFileManager_COMPANY_R04_staging.zip"
    set "FINAL_DIR=%DIST_DIR%\SunriseFileManager_Portable_COMPANY_R04"
    set "FINAL_ZIP=%DIST_DIR%\SunriseFileManager_Windows_x64_Portable_FullSource_COMPANY_R04.zip"
)
set "FAILED_STAGE=PREFLIGHT"

if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"
if not exist "%DIST_DIR%" mkdir "%DIST_DIR%"
set "RESULT_FILE=%LOG_DIR%\BUILD_RESULT.txt"
> "%RESULT_FILE%" echo STATUS=RUNNING
>> "%RESULT_FILE%" echo STAGE=PREFLIGHT

echo ============================================================================
echo  Sunrise File Manager - Windows x64 Portable Full-Source Build
echo  .NET 10 WPF / CMD only / Ollama is OPTIONAL
echo  Working Directory: %ROOT%
echo ============================================================================
echo.
if "%COMPANY_MODE%"=="1" (
    echo [COMPANY POLICY] Document and fixture access tests DISABLED.
    echo [COMPANY POLICY] No file content preview, PDF or HWPX test execution.
    echo [COMPANY POLICY] Office/PDF fixtures excluded from portable package.
    echo [COMPANY POLICY] WPF compilation and self-contained publishing remain ENABLED.
    echo.
)

if not exist "%ROOT%\source\Sunrise.FileManager\Sunrise.FileManager.csproj" goto :fail_missing_source
if not exist "%ROOT%\source\tests\LogicTests\LogicTests.csproj" goto :fail_missing_source
if not exist "%ROOT%\source\global.json" goto :fail_missing_source

set "DOTNET_EXE="
set "FOUND_SDK_VER="
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
set "DOTNET_CLI_UI_LANGUAGE=en"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"

REM Probe each dotnet host until one actually reports a 10.x SDK.
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
"%DOTNET_EXE%" --list-sdks > "%LOG_DIR%\sdk_list.log" 2>&1
"%DOTNET_EXE%" --info > "%LOG_DIR%\sdk_info.log" 2>&1
if errorlevel 1 goto :fail_sdk
> "%LOG_DIR%\selected_dotnet_path.txt" echo %DOTNET_EXE%

echo [INFO] Selected .NET SDK: "%DOTNET_EXE%" ^(SDK %FOUND_SDK_VER%^)
echo [INFO] Target Frameworks: LogicTests=net10.0 / WPF=net10.0-windows10.0.19041.0
echo [INFO] .NET runtime will be bundled (self-contained win-x64).
echo.

set "FAILED_STAGE=LOGIC_TESTS"
> "%RESULT_FILE%" echo STATUS=RUNNING
>> "%RESULT_FILE%" echo STAGE=LOGIC_TESTS

if "%COMPANY_MODE%"=="1" (
    echo [1/4] SKIPPED: LogicTests and document content tests ^(company policy^).
    > "%LOG_DIR%\01_logic_tests.log" echo SKIPPED=COMPANY_DOCUMENT_ACCESS_POLICY
    >> "%RESULT_FILE%" echo LOGIC_TESTS=SKIPPED_COMPANY_POLICY
) else (
    echo [1/4] Running Logic and Safety Unit Tests...
    "%DOTNET_EXE%" run --project "%ROOT%\source\tests\LogicTests\LogicTests.csproj" --configuration Release --no-launch-profile > "%LOG_DIR%\01_logic_tests.log" 2>&1
    if errorlevel 1 goto :fail_logic
    echo       PASS: LogicTests
)
echo.

set "FAILED_STAGE=WPF_BUILD"
> "%RESULT_FILE%" echo STATUS=RUNNING
>> "%RESULT_FILE%" echo STAGE=WPF_BUILD

echo [2/4] Building Sunrise.FileManager WPF Application...
"%DOTNET_EXE%" build "%ROOT%\source\Sunrise.FileManager\Sunrise.FileManager.csproj" --configuration Release -p:SunriseCompanyBuild=%COMPANY_MODE% > "%LOG_DIR%\02_wpf_build.log" 2>&1
if errorlevel 1 goto :fail_build
echo       PASS: WPF build
echo.

set "FAILED_STAGE=WIN_X64_PUBLISH"
> "%RESULT_FILE%" echo STATUS=RUNNING
>> "%RESULT_FILE%" echo STAGE=WIN_X64_PUBLISH

if exist "%STAGE_DIR%" rmdir /s /q "%STAGE_DIR%"
if exist "%STAGE_DIR%" goto :fail_publish
if exist "%STAGE_ZIP%" del /f /q "%STAGE_ZIP%"
mkdir "%STAGE_DIR%\app"
if errorlevel 1 goto :fail_publish

echo [3/4] Publishing Windows x64 Executable (self-contained)...
"%DOTNET_EXE%" publish "%ROOT%\source\Sunrise.FileManager\Sunrise.FileManager.csproj" --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=false -p:SunriseCompanyBuild=%COMPANY_MODE% --output "%STAGE_DIR%\app" > "%LOG_DIR%\03_wpf_publish.log" 2>&1
if errorlevel 1 goto :fail_publish
if not exist "%STAGE_DIR%\app\Sunrise.FileManager.exe" goto :fail_publish
if not exist "%STAGE_DIR%\app\Sunrise.FileManager.dll" goto :fail_publish
if not exist "%STAGE_DIR%\app\hostfxr.dll" goto :fail_publish
if not exist "%STAGE_DIR%\app\coreclr.dll" goto :fail_publish
if not exist "%STAGE_DIR%\app\PresentationFramework.dll" goto :fail_publish
echo       PASS: Self-contained Windows executable and runtime payload
echo.

set "FAILED_STAGE=PORTABLE_PACKAGE"
> "%RESULT_FILE%" echo STATUS=RUNNING
>> "%RESULT_FILE%" echo STAGE=PORTABLE_PACKAGE

echo [4/4] Packing full source with Portable application...
if "%COMPANY_MODE%"=="1" (
    REM Do not read/copy document fixtures into the company package.
    REM Exclude office/PDF documents even if they are added to source later.
    robocopy "%ROOT%\source" "%STAGE_DIR%\source" /E /XD bin obj .vs Fixtures /XF *.pdf *.hwp *.hwpx *.doc *.docx *.xls *.xlsx *.ppt *.pptx *.odt *.ods *.odp *.rtf /NFL /NDL /NJH /NJS /NP > "%LOG_DIR%\04_source_copy.log" 2>&1
) else (
    robocopy "%ROOT%\source" "%STAGE_DIR%\source" /E /XD bin obj .vs /NFL /NDL /NJH /NJS /NP > "%LOG_DIR%\04_source_copy.log" 2>&1
)
if errorlevel 8 goto :fail_package
for %%F in ("%ROOT%\*.cmd" "%ROOT%\*.md" "%ROOT%\*.json" "%ROOT%\LICENSE") do if exist "%%~F" copy /y "%%~F" "%STAGE_DIR%\" >nul
if not exist "%STAGE_DIR%\source\Sunrise.FileManager\Sunrise.FileManager.csproj" goto :fail_package
if not exist "%STAGE_DIR%\BUILD_ON_WINDOWS.cmd" goto :fail_package
if not exist "%STAGE_DIR%\BUILD_ON_WINDOWS_COMPANY.cmd" goto :fail_package
if not exist "%STAGE_DIR%\INSTALL_OLLAMA.cmd" goto :fail_package
if "%COMPANY_MODE%"=="1" (
    if exist "%STAGE_DIR%\source\tests\LogicTests\Fixtures\river_report.pdf" goto :fail_package
    > "%STAGE_DIR%\COMPANY_SOURCE_ONLY.flag" echo COMPANY_POLICY=NO_DOCUMENT_TESTS
    > "%STAGE_DIR%\COMPANY_BUILD_POLICY.txt" echo MODE=COMPANY
    >> "%STAGE_DIR%\COMPANY_BUILD_POLICY.txt" echo DOCUMENT_TESTS=SKIPPED
    >> "%STAGE_DIR%\COMPANY_BUILD_POLICY.txt" echo DOCUMENT_FIXTURES=EXCLUDED
    >> "%STAGE_DIR%\COMPANY_BUILD_POLICY.txt" echo WPF_COMPILE=REQUIRED
    >> "%STAGE_DIR%\COMPANY_BUILD_POLICY.txt" echo SELF_CONTAINED_PUBLISH=REQUIRED
)

(
    echo @echo off
    echo cd /d "%%~dp0"
    echo start "" "%%~dp0app\Sunrise.FileManager.exe"
) > "%STAGE_DIR%\START_FILE_MANAGER.cmd"
if not exist "%STAGE_DIR%\START_FILE_MANAGER.cmd" goto :fail_package

where.exe tar.exe >nul 2>&1
if errorlevel 1 goto :fail_tar
tar.exe -a -c -f "%STAGE_ZIP%" -C "%STAGE_DIR%" . > "%LOG_DIR%\04_package_zip.log" 2>&1
if errorlevel 1 goto :fail_package
if not exist "%STAGE_ZIP%" goto :fail_package
for %%Z in ("%STAGE_ZIP%") do if %%~zZ LEQ 0 goto :fail_package
tar.exe -t -f "%STAGE_ZIP%" > "%LOG_DIR%\04_zip_contents.log" 2>&1
if errorlevel 1 goto :fail_package
if "%COMPANY_MODE%"=="1" (
    findstr /I /R /C:"\.pdf$" /C:"\.hwp$" /C:"\.hwpx$" /C:"\.doc$" /C:"\.docx$" /C:"\.xls$" /C:"\.xlsx$" /C:"\.ppt$" /C:"\.pptx$" /C:"\.rtf$" "%LOG_DIR%\04_zip_contents.log" >nul
    if not errorlevel 1 goto :fail_package
)
findstr /I /C:"app/Sunrise.FileManager.exe" "%LOG_DIR%\04_zip_contents.log" >nul
if errorlevel 1 goto :fail_package
findstr /I /C:"source/Sunrise.FileManager/Sunrise.FileManager.csproj" "%LOG_DIR%\04_zip_contents.log" >nul
if errorlevel 1 goto :fail_package

REM Commit only after both folder and ZIP are staged and verified.
REM Old R02 output is preserved under _previous until new output is complete.
set "BACKUP_DIR=%DIST_DIR%\_SunriseFileManager_R02_previous"
set "BACKUP_ZIP=%DIST_DIR%\_SunriseFileManager_R02_previous.zip"
if "%COMPANY_MODE%"=="1" (
    set "BACKUP_DIR=%DIST_DIR%\_SunriseFileManager_COMPANY_R04_previous"
    set "BACKUP_ZIP=%DIST_DIR%\_SunriseFileManager_COMPANY_R04_previous.zip"
)
if exist "%BACKUP_DIR%" rmdir /s /q "%BACKUP_DIR%"
if exist "%BACKUP_ZIP%" del /f /q "%BACKUP_ZIP%"
if exist "%FINAL_DIR%" move "%FINAL_DIR%" "%BACKUP_DIR%" >nul
if exist "%FINAL_DIR%" goto :fail_restore_folder
if exist "%FINAL_ZIP%" move "%FINAL_ZIP%" "%BACKUP_ZIP%" >nul
if exist "%FINAL_ZIP%" goto :fail_restore_folder
move "%STAGE_DIR%" "%FINAL_DIR%" >nul
if errorlevel 1 goto :fail_restore_folder
move "%STAGE_ZIP%" "%FINAL_ZIP%" >nul
if errorlevel 1 goto :fail_restore_output
if not exist "%FINAL_DIR%\app\Sunrise.FileManager.exe" goto :fail_restore_output
if not exist "%FINAL_ZIP%" goto :fail_restore_output
if exist "%BACKUP_DIR%" rmdir /s /q "%BACKUP_DIR%"
if exist "%BACKUP_ZIP%" del /f /q "%BACKUP_ZIP%"

> "%RESULT_FILE%" echo STATUS=SUCCESS
>> "%RESULT_FILE%" echo STAGE=COMPLETE
>> "%RESULT_FILE%" echo MODE=%COMPANY_MODE%
if "%COMPANY_MODE%"=="1" >> "%RESULT_FILE%" echo LOGIC_TESTS=SKIPPED_COMPANY_POLICY
>> "%RESULT_FILE%" echo SDK=%FOUND_SDK_VER%
>> "%RESULT_FILE%" echo EXE=%FINAL_DIR%\app\Sunrise.FileManager.exe
>> "%RESULT_FILE%" echo ZIP=%FINAL_ZIP%
echo ============================================================================
echo [BUILD SUCCESS] Full-source Portable package is ready.
echo  EXE : "%FINAL_DIR%\app\Sunrise.FileManager.exe"
echo  ZIP : "%FINAL_ZIP%"
echo ============================================================================
if "%NO_PAUSE%"=="0" pause
exit /b 0

:probe_sdk
set "SDK_CANDIDATE=%~1"
"%SDK_CANDIDATE%" --list-sdks > "%LOG_DIR%\sdk_probe_candidate.log" 2>&1
if errorlevel 1 exit /b 0
set "SDK_MATCH="
for /f "tokens=1" %%V in ('findstr /R /B /C:"10\." "%LOG_DIR%\sdk_probe_candidate.log" 2^>nul') do if not defined SDK_MATCH set "SDK_MATCH=%%V"
if not defined SDK_MATCH exit /b 0
set "DOTNET_EXE=%SDK_CANDIDATE%"
set "FOUND_SDK_VER=%SDK_MATCH%"
exit /b 0

:fail_missing_source
set "FAILED_STAGE=PREFLIGHT_SOURCE"
echo [ERROR] Missing required C# project or global.json.
goto :fail_common
:fail_sdk
set "FAILED_STAGE=PREFLIGHT_DOTNET_10"
echo [ERROR] A compatible .NET 10 SDK was not found.
echo [INFO] Run CHECK_SDK.cmd and inspect build-logs\sdk_probe_candidate.log.
goto :fail_common
:fail_logic
echo [ERROR] LogicTests failed. See build-logs\01_logic_tests.log.
type "%LOG_DIR%\01_logic_tests.log"
goto :fail_common
:fail_build
echo [ERROR] WPF build failed. See build-logs\02_wpf_build.log.
type "%LOG_DIR%\02_wpf_build.log"
goto :fail_common
:fail_publish
echo [ERROR] Self-contained Windows publish failed. See build-logs\03_wpf_publish.log.
if exist "%LOG_DIR%\03_wpf_publish.log" type "%LOG_DIR%\03_wpf_publish.log"
goto :fail_common
:fail_tar
echo [ERROR] Windows tar.exe (ZIP packer) not found.
goto :fail_common
:fail_restore_folder
if exist "%BACKUP_DIR%" if not exist "%FINAL_DIR%" move "%BACKUP_DIR%" "%FINAL_DIR%" >nul
if exist "%BACKUP_ZIP%" if not exist "%FINAL_ZIP%" move "%BACKUP_ZIP%" "%FINAL_ZIP%" >nul
goto :fail_package
:fail_restore_output
if exist "%FINAL_ZIP%" del /f /q "%FINAL_ZIP%"
if exist "%FINAL_DIR%" rmdir /s /q "%FINAL_DIR%"
if exist "%BACKUP_DIR%" move "%BACKUP_DIR%" "%FINAL_DIR%" >nul
if exist "%BACKUP_ZIP%" if not exist "%FINAL_ZIP%" move "%BACKUP_ZIP%" "%FINAL_ZIP%" >nul
goto :fail_package
:fail_package
echo [ERROR] Portable source packaging or archive verification failed.
echo [INFO] See build-logs\04_source_copy.log and 04_package_zip.log.
goto :fail_common
:fail_common
> "%RESULT_FILE%" echo STATUS=FAILED
>> "%RESULT_FILE%" echo STAGE=%FAILED_STAGE%
>> "%RESULT_FILE%" echo MODE=%COMPANY_MODE%
if "%COMPANY_MODE%"=="1" >> "%RESULT_FILE%" echo LOGIC_TESTS=SKIPPED_COMPANY_POLICY
>> "%RESULT_FILE%" echo SDK=%FOUND_SDK_VER%
if exist "%STAGE_DIR%" rmdir /s /q "%STAGE_DIR%"
if exist "%STAGE_ZIP%" del /f /q "%STAGE_ZIP%"
echo [BUILD FAILED] Stage: %FAILED_STAGE%
echo [INFO] See "%RESULT_FILE%" and build-logs for details.
if "%NO_PAUSE%"=="0" pause
exit /b 1
