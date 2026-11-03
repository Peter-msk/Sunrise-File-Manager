@echo off
setlocal EnableExtensions DisableDelayedExpansion
REM Package the ALREADY SIGNED, self-contained WPF app without another compile/sign.
REM Called by official installer and preview-only builds. Never installs or runs app.
if "%~1"=="" goto :usage
if "%~2"=="" goto :usage
set "SOURCE_APP=%~f1"
set "APP_VERSION=%~2"
set "ROOT=%~dp0.."
for %%R in ("%ROOT%") do set "ROOT=%%~fR"
set "DIST=%ROOT%\dist"
set "LOG_DIR=%ROOT%\build-logs"
set "RESULT=%LOG_DIR%\R17_PREVIEW_BUILD_RESULT.txt"
set "FINAL=%DIST%\Preview\Sunrise Group\Sunrise File Manager"
set "FINAL_ZIP=%DIST%\Sunrise_File_Manager_%APP_VERSION%_Preview.zip"
set "STAGE=%DIST%\_SFM_preview_staging"
set "ZIP_STAGE=%DIST%\_SFM_preview_staging.zip"
set "ZIP_LAYOUT=%DIST%\_SFM_preview_group_layout"
set "BACKUP=%DIST%\_SFM_preview_previous"
set "BACKUP_ZIP=%DIST%\_SFM_preview_previous.zip"
set "FAILED_STAGE=VALIDATION"
if not exist "%DIST%" mkdir "%DIST%"
if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"
> "%RESULT%" echo STATUS=RUNNING
>> "%RESULT%" echo STAGE=VALIDATION
if not exist "%SOURCE_APP%\Sunrise.FileManager.exe" goto :fail
if not exist "%SOURCE_APP%\Sunrise.FileManager.dll" goto :fail
for %%F in (hostfxr.dll coreclr.dll PresentationFramework.dll Sunrise.FileManager.runtimeconfig.json) do if not exist "%SOURCE_APP%\%%F" goto :fail
REM The caller MUST have signed and verified both PE files using the pinned signer.
REM Do not re-sign copied files or relax security checks in preview mode.
if exist "%STAGE%" rmdir /s /q "%STAGE%"
if exist "%ZIP_STAGE%" del /q /f "%ZIP_STAGE%"
if exist "%ZIP_LAYOUT%" rmdir /s /q "%ZIP_LAYOUT%"
if exist "%STAGE%" goto :fail
if exist "%ZIP_STAGE%" goto :fail
set "FAILED_STAGE=COPY"
robocopy "%SOURCE_APP%" "%STAGE%" /E /R:1 /W:1 /XJ /NFL /NDL /NJH /NJS /NP > "%LOG_DIR%\R17_preview_copy.log" 2>&1
if errorlevel 8 goto :fail
for %%F in (Sunrise.FileManager.exe Sunrise.FileManager.dll hostfxr.dll coreclr.dll PresentationFramework.dll Sunrise.FileManager.runtimeconfig.json) do if not exist "%STAGE%\%%F" goto :fail
REM Detect a truncated/corrupted copy of the signed executable or main assembly.
fc /b "%SOURCE_APP%\Sunrise.FileManager.exe" "%STAGE%\Sunrise.FileManager.exe" > "%LOG_DIR%\R17_preview_exe_compare.log" 2>&1
if errorlevel 1 goto :fail
fc /b "%SOURCE_APP%\Sunrise.FileManager.dll" "%STAGE%\Sunrise.FileManager.dll" > "%LOG_DIR%\R17_preview_dll_compare.log" 2>&1
if errorlevel 1 goto :fail
REM No new launcher executable is needed: the signed Sunrise.FileManager.exe is at root.
REM The full folder must remain together; no additional .NET/WPF install required.
(
 echo Sunrise File Manager %APP_VERSION% - install-free preview
 echo.
 echo Double-click Sunrise.FileManager.exe in this folder.
 echo Keep this executable and all neighboring files/folders together.
 echo Windows x64. The required .NET Desktop Runtime is bundled.
 echo Close any running Sunrise File Manager before starting this preview.
 echo This runs the real application. Changes to selected files and #memo are real.
 echo Preview and installed versions may use the same user preferences.
 echo Sunrise Control and normal update/notice policies still apply.
 echo No Windows installation is performed by opening this executable.
 echo This preview ZIP is not a public updater or an installer.
) > "%STAGE%\README_PREVIEW.txt"
if exist "%ROOT%\LICENSE" copy /y "%ROOT%\LICENSE" "%STAGE%\LICENSE" >nul
set "FAILED_STAGE=ZIP"
where.exe tar.exe >nul 2>&1
if errorlevel 1 goto :fail
REM Distributable root MUST contain Sunrise Group\Sunrise File Manager\...
mkdir "%ZIP_LAYOUT%\Sunrise Group\Sunrise File Manager"
if errorlevel 1 goto :fail
robocopy "%STAGE%" "%ZIP_LAYOUT%\Sunrise Group\Sunrise File Manager" /E /R:1 /W:1 /XJ /NFL /NDL /NJH /NJS /NP > "%LOG_DIR%\R17_preview_group_copy.log" 2>&1
if errorlevel 8 goto :fail
tar.exe -a -c -f "%ZIP_STAGE%" -C "%ZIP_LAYOUT%" . > "%LOG_DIR%\R17_preview_zip_create.log" 2>&1
if errorlevel 1 goto :fail
if not exist "%ZIP_STAGE%" goto :fail
for %%F in ("%ZIP_STAGE%") do if %%~zF LEQ 0 goto :fail
tar.exe -t -f "%ZIP_STAGE%" > "%LOG_DIR%\R17_preview_zip_contents.log" 2>&1
if errorlevel 1 goto :fail
findstr /I /C:"Sunrise Group/Sunrise File Manager/Sunrise.FileManager.exe" "%LOG_DIR%\R17_preview_zip_contents.log" >nul
if errorlevel 1 goto :fail
findstr /I /C:"README_PREVIEW.txt" "%LOG_DIR%\R17_preview_zip_contents.log" >nul
if errorlevel 1 goto :fail
REM No source, tests, fixtures, credentials or installer metadata in preview distribution.
findstr /I /R /C:"\.cs$" /C:"\.xaml$" /C:"source/" /C:"tests/" /C:"Fixtures/" /C:"\.pfx$" /C:"\.p12$" "%LOG_DIR%\R17_preview_zip_contents.log" >nul
if not errorlevel 1 goto :fail
set "FAILED_STAGE=COMMIT"
REM Keep the last working preview until both new artifacts are complete.
if exist "%BACKUP%" rmdir /s /q "%BACKUP%"
if exist "%BACKUP_ZIP%" del /q /f "%BACKUP_ZIP%"
if exist "%BACKUP%" goto :fail
if exist "%BACKUP_ZIP%" goto :fail
if exist "%FINAL%" (
 move "%FINAL%" "%BACKUP%" >nul
 if errorlevel 1 goto :fail
)
if exist "%FINAL_ZIP%" (
 move "%FINAL_ZIP%" "%BACKUP_ZIP%" >nul
 if errorlevel 1 goto :restore
)
if not exist "%DIST%\Preview\Sunrise Group" mkdir "%DIST%\Preview\Sunrise Group"
if not exist "%DIST%\Preview\Sunrise Group" goto :restore
move "%STAGE%" "%FINAL%" >nul
if errorlevel 1 goto :restore
move "%ZIP_STAGE%" "%FINAL_ZIP%" >nul
if errorlevel 1 goto :restore
if not exist "%FINAL%\Sunrise.FileManager.exe" goto :restore
if not exist "%FINAL_ZIP%" goto :restore
set "FAILED_STAGE=HASH"
certutil -hashfile "%FINAL%\Sunrise.FileManager.exe" SHA256 > "%LOG_DIR%\R17_PREVIEW_EXE_SHA256.txt" 2>&1
if errorlevel 1 goto :restore
certutil -hashfile "%FINAL_ZIP%" SHA256 > "%LOG_DIR%\R17_PREVIEW_ZIP_SHA256.txt" 2>&1
if errorlevel 1 goto :restore
if exist "%BACKUP%" rmdir /s /q "%BACKUP%"
if exist "%BACKUP_ZIP%" del /q /f "%BACKUP_ZIP%"
if exist "%ZIP_LAYOUT%" rmdir /s /q "%ZIP_LAYOUT%"
> "%RESULT%" echo STATUS=SUCCESS
>> "%RESULT%" echo VERSION=%APP_VERSION%
>> "%RESULT%" echo TYPE=SIGNED_SELF_CONTAINED_ONEDIR
>> "%RESULT%" echo APP=%FINAL%\Sunrise.FileManager.exe
>> "%RESULT%" echo ZIP=%FINAL_ZIP%
>> "%RESULT%" echo SOURCE_SIGNED_PAYLOAD=%SOURCE_APP%
echo [PREVIEW] Signed install-free EXE: "%FINAL%\Sunrise.FileManager.exe"
echo [PREVIEW] Portable preview ZIP: "%FINAL_ZIP%"
exit /b 0
:restore
if exist "%FINAL%" rmdir /s /q "%FINAL%"
if exist "%FINAL_ZIP%" del /f /q "%FINAL_ZIP%"
if exist "%BACKUP%" move "%BACKUP%" "%FINAL%" >nul
if exist "%BACKUP_ZIP%" move "%BACKUP_ZIP%" "%FINAL_ZIP%" >nul
:fail
if exist "%ZIP_LAYOUT%" rmdir /s /q "%ZIP_LAYOUT%"
if exist "%STAGE%" rmdir /s /q "%STAGE%"
if exist "%ZIP_STAGE%" del /f /q "%ZIP_STAGE%"
> "%RESULT%" echo STATUS=FAILED
>> "%RESULT%" echo STAGE=%FAILED_STAGE%
echo [ERROR] Preview packaging failed at %FAILED_STAGE%. Previous preview, if any, was preserved.
exit /b 1
:usage
echo Usage: PackageSignedPreview.cmd signed-self-contained-app-directory version
exit /b 2
