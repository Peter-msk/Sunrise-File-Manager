@echo off
setlocal EnableExtensions DisableDelayedExpansion
REM R14: Company-safe OneDir build with Sunrise Group Authenticode signing.
REM It uses the normal .NET 10 WPF publish and keeps the runtime inside app\.
REM No document inspection is executed. Signatures are applied on BUILD PC ONLY.
set "NO_PAUSE=0"
if /I "%~1"=="--no-pause" set "NO_PAUSE=1"
if /I "%~2"=="--no-pause" set "NO_PAUSE=1"
set "COMPANY_MODE=0"
set "INSTALLER_PAYLOAD=0"
if /I "%~3"=="--installer-payload" set "INSTALLER_PAYLOAD=1"
if exist "%~dp0COMPANY_SOURCE_ONLY.flag" set "COMPANY_MODE=1"
if /I "%~1"=="--company" set "COMPANY_MODE=1"
if /I "%~2"=="--company" set "COMPANY_MODE=1"
cd /d "%~dp0"
if errorlevel 1 goto :fail_cd
set "ROOT=%CD%"
set "DIST=%ROOT%\dist"
set "LOG_DIR=%ROOT%\build-logs"
set "PROJECT=%ROOT%\source\Sunrise.FileManager\Sunrise.FileManager.csproj"
set "STAGE=%DIST%\_SFM_R14_onedir_staging"
REM Installer-only builds do not need a standalone OneDir ZIP or previous portable release.
if "%INSTALLER_PAYLOAD%"=="1" set "STAGE=%DIST%\_R18_installer_payload"
set "ZIP_STAGE=%DIST%\_SFM_R14_onedir_staging.zip"
set "FINAL=%DIST%\SunriseFileManager_OneDir_R14"
set "FINAL_ZIP=%DIST%\SunriseFileManager_OneDir_Windows_x64_R14.zip"
set "PREVIOUS=%DIST%\_SFM_R14_onedir_previous"
set "PREVIOUS_ZIP=%DIST%\_SFM_R14_onedir_previous.zip"
set "RESULT=%LOG_DIR%\R14_BUILD_RESULT.txt"
set "SIGNING_SCRIPT=%ROOT%\build-tools\Sunrise.GroupSigning.ps1"
set "POWERSHELL_EXE=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
set "CERT_FILE=%LOG_DIR%\R14_signing_certificate.txt"
if not defined SUNRISE_RELEASE_VERSION set "SUNRISE_RELEASE_VERSION=1.0.0"
set "TIMESTAMP_URL=http://timestamp.digicert.com"
if defined SUNRISE_TIMESTAMP_URL set "TIMESTAMP_URL=%SUNRISE_TIMESTAMP_URL%"
set "SIGNTOOL="
set "CERT_THUMBPRINT="
set "FAILED_STAGE=PREFLIGHT"
if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"
if not exist "%DIST%" mkdir "%DIST%"
> "%RESULT%" echo STATUS=RUNNING
>> "%RESULT%" echo STAGE=PREFLIGHT
>> "%RESULT%" echo FORMAT=ONEDIR_SELF_CONTAINED
>> "%RESULT%" echo COMPANY_MODE=%COMPANY_MODE%
if not exist "%PROJECT%" goto :fail_project
set "DOTNET_EXE="
set "FOUND_SDK_VER="
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
set "DOTNET_CLI_UI_LANGUAGE=en"
for %%D in (
 "%ProgramW6432%\dotnet\dotnet.exe"
 "%ProgramFiles%\dotnet\dotnet.exe"
 "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
 "%USERPROFILE%\.dotnet\dotnet.exe"
) do if not defined DOTNET_EXE if exist "%%~D" call :probe_sdk "%%~D"
for /f "delims=" %%D in ('where.exe dotnet.exe 2^>nul') do if not defined DOTNET_EXE call :probe_sdk "%%~D"
if not defined DOTNET_EXE goto :fail_sdk
for %%I in ("%DOTNET_EXE%") do set "DOTNET_ROOT=%%~dpI"
set "PATH=%DOTNET_ROOT%;%PATH%"
"%DOTNET_EXE%" --list-sdks > "%LOG_DIR%\R14_sdk_list.log" 2>&1
if errorlevel 1 goto :fail_sdk
>> "%RESULT%" echo SDK=%FOUND_SDK_VER%
echo [INFO] .NET 10 SDK: %FOUND_SDK_VER%
echo [INFO] OneDir / win-x64 / self-contained / Sunrise Group Authenticode required
echo [INFO] Company mode: %COMPANY_MODE%
if "%COMPANY_MODE%"=="1" echo [COMPANY POLICY] LogicTests and document fixture access SKIPPED.
REM Build-time only: discover SignTool. Never copy credentials or tools into user package.
set "FAILED_STAGE=SIGNING_PREFLIGHT"
>> "%RESULT%" echo STAGE=SIGNING_PREFLIGHT
if not exist "%SIGNING_SCRIPT%" goto :fail_signing
if not exist "%POWERSHELL_EXE%" goto :fail_signing
for /f "delims=" %%S in ('where.exe signtool.exe 2^>nul') do if not defined SIGNTOOL if exist "%%~S" set "SIGNTOOL=%%~S"
if not defined SIGNTOOL for /d %%V in ("%ProgramFiles(x86)%\Windows Kits\10\bin\10.*") do if exist "%%~V\x64\signtool.exe" set "SIGNTOOL=%%~V\x64\signtool.exe"
if not defined SIGNTOOL if exist "%ProgramFiles%\Windows Kits\10\bin" for /d %%V in ("%ProgramFiles%\Windows Kits\10\bin\10.*") do if exist "%%~V\x64\signtool.exe" set "SIGNTOOL=%%~V\x64\signtool.exe"
if not defined SIGNTOOL goto :fail_signing
REM Security suite may flag a newly generated, unsigned Sunrise.Signing.exe.
REM Use Windows PowerShell certificate APIs for preflight, not a custom generated EXE.
REM Respect corporate script policies: do NOT override ExecutionPolicy or V3 settings.
if "%SUNRISE_ALLOW_CERT_CREATE%"=="1" (
  echo [ERROR] Build-time certificate generation is disabled. Ask IT to provision the existing group certificate.
  goto :fail_signing
)
if exist "%CERT_FILE%" del /f /q "%CERT_FILE%"
"%POWERSHELL_EXE%" -NoLogo -NoProfile -NonInteractive -File "%SIGNING_SCRIPT%" -Action Prepare -Path "%CERT_FILE%" > "%LOG_DIR%\R14_signing_preflight.log" 2>&1
if errorlevel 1 goto :fail_signing
for /f "usebackq tokens=1,2 delims==" %%A in ("%CERT_FILE%") do if /I "%%A"=="THUMBPRINT" set "CERT_THUMBPRINT=%%B"
if not defined CERT_THUMBPRINT goto :fail_signing
echo [SIGNING] Reusing CN=Sunrise Group certificate from CurrentUser/My.
echo [SIGNING] Timestamp: %TIMESTAMP_URL%
set "FAILED_STAGE=WPF_PUBLISH"
>> "%RESULT%" echo STAGE=WPF_PUBLISH
if exist "%STAGE%" rmdir /s /q "%STAGE%"
if exist "%STAGE%" goto :fail_release
if exist "%ZIP_STAGE%" del /q /f "%ZIP_STAGE%"
mkdir "%STAGE%\app"
if errorlevel 1 goto :fail_release
REM Publish compiles WPF itself. No second redundant dotnet build or document test run.
"%DOTNET_EXE%" publish "%PROJECT%" --configuration Release --framework net10.0-windows10.0.19041.0 --runtime win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:Version=%SUNRISE_RELEASE_VERSION% -p:SunriseCompanyBuild=%COMPANY_MODE% --output "%STAGE%\app" > "%LOG_DIR%\R14_wpf_publish.log" 2>&1
if errorlevel 1 goto :fail_release
if not exist "%STAGE%\app\Sunrise.FileManager.exe" goto :fail_release
if not exist "%STAGE%\app\Sunrise.FileManager.dll" goto :fail_release
if not exist "%STAGE%\app\hostfxr.dll" goto :fail_release
if not exist "%STAGE%\app\coreclr.dll" goto :fail_release
if not exist "%STAGE%\app\PresentationFramework.dll" goto :fail_release
REM Sign only Sunrise-owned PE files. Microsoft/.NET/PdfPig files remain untouched.
set "FAILED_STAGE=AUTHENTICODE_SIGN"
>> "%RESULT%" echo STAGE=AUTHENTICODE_SIGN
"%SIGNTOOL%" sign /fd SHA256 /sha1 "%CERT_THUMBPRINT%" /s My /tr "%TIMESTAMP_URL%" /td SHA256 /v "%STAGE%\app\Sunrise.FileManager.exe" > "%LOG_DIR%\R14_sign_exe.log" 2>&1
if errorlevel 1 goto :fail_signing
"%SIGNTOOL%" sign /fd SHA256 /sha1 "%CERT_THUMBPRINT%" /s My /tr "%TIMESTAMP_URL%" /td SHA256 /v "%STAGE%\app\Sunrise.FileManager.dll" > "%LOG_DIR%\R14_sign_dll.log" 2>&1
if errorlevel 1 goto :fail_signing
set "FAILED_STAGE=SIGNATURE_CHECK"
>> "%RESULT%" echo STAGE=SIGNATURE_CHECK
"%POWERSHELL_EXE%" -NoLogo -NoProfile -NonInteractive -File "%SIGNING_SCRIPT%" -Action VerifyEmbedded -Path "%STAGE%\app\Sunrise.FileManager.exe" -Thumbprint "%CERT_THUMBPRINT%" > "%LOG_DIR%\R14_verify_exe.log" 2>&1
if errorlevel 1 goto :fail_signing
"%POWERSHELL_EXE%" -NoLogo -NoProfile -NonInteractive -File "%SIGNING_SCRIPT%" -Action VerifyEmbedded -Path "%STAGE%\app\Sunrise.FileManager.dll" -Thumbprint "%CERT_THUMBPRINT%" > "%LOG_DIR%\R14_verify_dll.log" 2>&1
if errorlevel 1 goto :fail_signing
REM Untrusted self-signed certs can fail Windows trust-chain validation. Keep report honest.
"%SIGNTOOL%" verify /pa /v "%STAGE%\app\Sunrise.FileManager.exe" > "%LOG_DIR%\R14_wintrust_exe.log" 2>&1
if errorlevel 1 (
  findstr /I /C:"0x800B0109" /C:"0x800B010A" /C:"not trusted" /C:"untrusted root" "%LOG_DIR%\R14_wintrust_exe.log" >nul
  if errorlevel 1 goto :fail_signing
  >> "%RESULT%" echo WINTRUST=UNTRUSTED_SELF_SIGNED
) else (
  >> "%RESULT%" echo WINTRUST=PASS
)
"%SIGNTOOL%" verify /pa /v "%STAGE%\app\Sunrise.FileManager.dll" > "%LOG_DIR%\R14_wintrust_dll.log" 2>&1
if errorlevel 1 (
  findstr /I /C:"0x800B0109" /C:"0x800B010A" /C:"not trusted" /C:"untrusted root" "%LOG_DIR%\R14_wintrust_dll.log" >nul
  if errorlevel 1 goto :fail_signing
  >> "%RESULT%" echo DLL_WINTRUST=UNTRUSTED_SELF_SIGNED
) else (
  >> "%RESULT%" echo DLL_WINTRUST=PASS
)
certutil -hashfile "%STAGE%\app\Sunrise.FileManager.exe" SHA256 > "%LOG_DIR%\R14_signed_exe_sha256.txt" 2>&1
if errorlevel 1 goto :fail_signing
certutil -hashfile "%STAGE%\app\Sunrise.FileManager.dll" SHA256 > "%LOG_DIR%\R14_signed_dll_sha256.txt" 2>&1
if errorlevel 1 goto :fail_signing
if "%INSTALLER_PAYLOAD%"=="1" goto :installer_payload_ready
set "FAILED_STAGE=PACKAGE"
>> "%RESULT%" echo STAGE=PACKAGE
REM Clear root entry point: user never has to search inside hundreds of dependencies.
(
 echo @echo off
 echo start "" "%%~dp0app\Sunrise.FileManager.exe"
) > "%STAGE%\Sunrise File Manager.cmd"
if not exist "%STAGE%\Sunrise File Manager.cmd" goto :fail_release
(
 echo Sunrise File Manager - R14 Windows x64 OneDir
 echo Double-click "Sunrise File Manager.cmd" in this folder to run the app.
 echo The app\ directory contains necessary .NET/WPF runtime files. Do not delete them.
 echo No .NET SDK is required on the target PC. Ollama is optional.
 echo Development source, tests and fixtures are distributed separately.
) > "%STAGE%\READ_ME_FIRST.txt"
if exist "%ROOT%\LICENSE" copy /y "%ROOT%\LICENSE" "%STAGE%\LICENSE" >nul
if exist "%ROOT%\source\licenses" (
  robocopy "%ROOT%\source\licenses" "%STAGE%\licenses" /E /NFL /NDL /NJH /NJS /NP > "%LOG_DIR%\R14_licenses.log" 2>&1
  if errorlevel 8 goto :fail_release
)
where.exe tar.exe >nul 2>&1
if errorlevel 1 goto :fail_release
tar.exe -a -c -f "%ZIP_STAGE%" -C "%STAGE%" . > "%LOG_DIR%\R14_zip.log" 2>&1
if errorlevel 1 goto :fail_release
if not exist "%ZIP_STAGE%" goto :fail_release
for %%Z in ("%ZIP_STAGE%") do if %%~zZ LEQ 0 goto :fail_release
tar.exe -t -f "%ZIP_STAGE%" > "%LOG_DIR%\R14_zip_contents.log" 2>&1
if errorlevel 1 goto :fail_release
findstr /I /C:"app/Sunrise.FileManager.exe" "%LOG_DIR%\R14_zip_contents.log" >nul
if errorlevel 1 goto :fail_release
findstr /I /C:"Sunrise File Manager.cmd" "%LOG_DIR%\R14_zip_contents.log" >nul
if errorlevel 1 goto :fail_release
findstr /I /R /C:"\.cs$" /C:"\.xaml$" /C:"source/" /C:"Fixtures/" /C:"\.pdf$" /C:"\.hwpx$" "%LOG_DIR%\R14_zip_contents.log" >nul
if not errorlevel 1 goto :fail_release
REM Only replace a previous successful release after the new output has been checked.
set "FAILED_STAGE=COMMIT"
if exist "%PREVIOUS%" rmdir /s /q "%PREVIOUS%"
if exist "%PREVIOUS_ZIP%" del /f /q "%PREVIOUS_ZIP%"
if exist "%FINAL%" move "%FINAL%" "%PREVIOUS%" >nul
if exist "%FINAL_ZIP%" move "%FINAL_ZIP%" "%PREVIOUS_ZIP%" >nul
move "%STAGE%" "%FINAL%" >nul
if errorlevel 1 goto :fail_restore
move "%ZIP_STAGE%" "%FINAL_ZIP%" >nul
if errorlevel 1 goto :fail_restore
if not exist "%FINAL%\Sunrise File Manager.cmd" goto :fail_restore
if not exist "%FINAL%\app\Sunrise.FileManager.exe" goto :fail_restore
if not exist "%FINAL_ZIP%" goto :fail_restore
certutil -hashfile "%FINAL_ZIP%" SHA256 > "%LOG_DIR%\R14_signed_package_sha256.txt" 2>&1
if errorlevel 1 goto :fail_restore
if exist "%PREVIOUS%" rmdir /s /q "%PREVIOUS%"
if exist "%PREVIOUS_ZIP%" del /f /q "%PREVIOUS_ZIP%"
> "%RESULT%" echo STATUS=SUCCESS
>> "%RESULT%" echo STAGE=COMPLETE
>> "%RESULT%" echo FORMAT=ONEDIR_SELF_CONTAINED
>> "%RESULT%" echo COMPANY_MODE=%COMPANY_MODE%
>> "%RESULT%" echo SDK=%FOUND_SDK_VER%
>> "%RESULT%" echo CERTIFICATE_SUBJECT=CN=Sunrise Group
>> "%RESULT%" echo SIGNED_EXECUTABLE=app\Sunrise.FileManager.exe
>> "%RESULT%" echo SIGNED_LIBRARY=app\Sunrise.FileManager.dll
>> "%RESULT%" echo START=%FINAL%\Sunrise File Manager.cmd
>> "%RESULT%" echo APP=%FINAL%\app\Sunrise.FileManager.exe
>> "%RESULT%" echo ZIP=%FINAL_ZIP%
echo [SUCCESS] OneDir released. Double-click the launcher in: 
echo "%FINAL%\Sunrise File Manager.cmd"
if "%NO_PAUSE%"=="0" pause
exit /b 0
:installer_payload_ready
REM Official installer consumes the signed app directory directly; no tar.exe dependency.
if not exist "%STAGE%\app\Sunrise.FileManager.exe" goto :fail_release
if not exist "%STAGE%\app\Sunrise.FileManager.dll" goto :fail_release
> "%RESULT%" echo STATUS=SUCCESS
>> "%RESULT%" echo STAGE=INSTALLER_PAYLOAD_READY
>> "%RESULT%" echo FORMAT=SIGNED_INSTALLER_PAYLOAD
>> "%RESULT%" echo COMPANY_MODE=%COMPANY_MODE%
>> "%RESULT%" echo APP=%STAGE%\app\Sunrise.FileManager.exe
>> "%RESULT%" echo GROUP_SIGNER=CN=Sunrise Group
echo [SUCCESS] Signed installer payload ready: "%STAGE%\app"
exit /b 0
:probe_sdk
if not exist "%~1" exit /b 0
for /f "tokens=1" %%S in ('"%~1" --list-sdks 2^>nul ^| findstr /R /C:"^10\."') do if not defined DOTNET_EXE (
  set "DOTNET_EXE=%~1"
  set "FOUND_SDK_VER=%%S"
)
exit /b 0
:fail_restore
if exist "%FINAL%" rmdir /s /q "%FINAL%"
if exist "%FINAL_ZIP%" del /f /q "%FINAL_ZIP%"
if exist "%PREVIOUS%" move "%PREVIOUS%" "%FINAL%" >nul
if exist "%PREVIOUS_ZIP%" move "%PREVIOUS_ZIP%" "%FINAL_ZIP%" >nul
:fail_release
if exist "%STAGE%" rmdir /s /q "%STAGE%"
if exist "%ZIP_STAGE%" del /f /q "%ZIP_STAGE%"
goto :fail
:fail_signing
echo [ERROR] Sunrise Group signing failed. Existing OneDir release was preserved.
echo [ERROR] Check R14_signing_preflight.log, R14_sign_exe.log, R14_sign_dll.log or R14_verify*.log.
echo [ERROR] SignTool, a valid CN=Sunrise Group certificate and permitted PowerShell script execution are required.
echo [ERROR] Do not change V3 exclusions or bypass PowerShell ExecutionPolicy; contact IT if blocked.
goto :fail_release
:fail_project
echo [ERROR] Missing WPF project: %PROJECT%
goto :fail
:fail_sdk
echo [ERROR] .NET 10 SDK was not found.
goto :fail
:fail_cd
echo [ERROR] Cannot access project directory.
exit /b 1
:fail
> "%RESULT%" echo STATUS=FAILED
>> "%RESULT%" echo STAGE=%FAILED_STAGE%
>> "%RESULT%" echo FORMAT=ONEDIR_SELF_CONTAINED
>> "%RESULT%" echo COMPANY_MODE=%COMPANY_MODE%
echo [ERROR] Stage %FAILED_STAGE% failed. See build-logs for details.
if "%NO_PAUSE%"=="0" pause
exit /b 1
