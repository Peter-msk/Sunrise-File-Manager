@echo off
setlocal EnableExtensions DisableDelayedExpansion
REM Sunrise File Manager R15 - official installer release. Run on Windows x64 only.
REM Compiles a signed WPF OneDir payload, wraps it in an Inno Setup installer,
REM signs the installer, verifies signer identity, then commits the final artifact.
REM No document inspection, GUI capture, silent installation or network updater.
cd /d "%~dp0"
if errorlevel 1 exit /b 1
set "ROOT=%CD%"
set "DIST=%ROOT%\dist"
set "LOG_DIR=%ROOT%\build-logs"
set "RESULT=%LOG_DIR%\R15_INSTALLER_BUILD_RESULT.txt"
set "VERSION_FILE=%ROOT%\installer\RELEASE_VERSION.txt"
set "ISS=%ROOT%\installer\SunriseFileManager.iss"
set "APP_DIR=%DIST%\_R18_installer_payload\app"
set "SIGNING_SCRIPT=%ROOT%\build-tools\Sunrise.GroupSigning.ps1"
set "POWERSHELL_EXE=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
set "CERT_FILE=%LOG_DIR%\R14_signing_certificate.txt"
set "INSTALLER_STAGE=%DIST%\_R15_installer_stage"
set "NO_PAUSE=0"
if /I "%~1"=="--no-pause" set "NO_PAUSE=1"
if /I "%~2"=="--no-pause" set "NO_PAUSE=1"
if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"
if not exist "%DIST%" mkdir "%DIST%"
set "FAILED_STAGE=PREFLIGHT"
> "%RESULT%" echo STATUS=RUNNING
>> "%RESULT%" echo STAGE=PREFLIGHT
>> "%RESULT%" echo FORMAT=INSTALLER_SETUP_EXE
>> "%RESULT%" echo COMPANY_LOGIC_TESTS=SKIPPED
if not exist "%ROOT%\COMPANY_SOURCE_ONLY.flag" goto :fail_company
REM Always report which preflight check failed; do not treat all errors as a missing SDK.
set "VERSION_PROBLEM="
set "VERSION_DIAG=%LOG_DIR%\R15_version_preflight.log"
> "%VERSION_DIAG%" echo STAGE=VERSION_PREFLIGHT
>> "%VERSION_DIAG%" echo VERSION_FILE=%VERSION_FILE%
>> "%VERSION_DIAG%" echo ISS_FILE=%ISS%
if not exist "%VERSION_FILE%" (
 set "VERSION_PROBLEM=VERSION_FILE_MISSING"
 goto :fail_version
)
if not exist "%ISS%" (
 set "VERSION_PROBLEM=INNO_SCRIPT_MISSING"
 goto :fail_version
)
REM Read the first non-empty line directly. Previous nested FINDSTR regex gave no
REM diagnostic when no line matched, even for a perfectly normal 1.0.0 file.
set "SFM_APP_VERSION="
for /f "usebackq delims=" %%V in ("%VERSION_FILE%") do if not defined SFM_APP_VERSION set "SFM_APP_VERSION=%%V"
if not defined SFM_APP_VERSION (
 set "VERSION_PROBLEM=VERSION_FILE_EMPTY"
 goto :fail_version
)
REM Validate that the exact value consists of THREE nonempty decimal segments.
REM Do not silently fall back to a different version or continue with bad data.
call :validate_release_version "%SFM_APP_VERSION%"
if errorlevel 1 (
 set "VERSION_PROBLEM=VERSION_INVALID_EXPECTED_THREE_NUMERIC_SEGMENTS"
 goto :fail_version
)
>> "%VERSION_DIAG%" echo CHECK=PASS
>> "%VERSION_DIAG%" echo VERSION=%SFM_APP_VERSION%
echo [VERSION] OK: %SFM_APP_VERSION% from installer\RELEASE_VERSION.txt
REM The release version is maintained in installer\RELEASE_VERSION.txt.
REM Do not modify it silently or derive it from the internal Rxx revision.
set "FINAL_SETUP=%DIST%\Sunrise_File_Manager_%SFM_APP_VERSION%_Setup.exe"
set "BACKUP_SETUP=%DIST%\_R15_previous_signed_setup.exe"
set "UNSIGNED_SETUP=%INSTALLER_STAGE%\Sunrise_File_Manager_%SFM_APP_VERSION%_Setup.exe"
set "SUNRISE_RELEASE_VERSION=%SFM_APP_VERSION%"
set "TIMESTAMP_URL=http://timestamp.digicert.com"
if defined SUNRISE_TIMESTAMP_URL set "TIMESTAMP_URL=%SUNRISE_TIMESTAMP_URL%"
set "ISCC="
for /f "delims=" %%I in ('where.exe ISCC.exe 2^>nul') do if not defined ISCC if exist "%%~I" set "ISCC=%%~I"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 7\ISCC.exe"
if not defined ISCC goto :fail_inno
if not exist "%SIGNING_SCRIPT%" goto :fail_signing_script
if not exist "%POWERSHELL_EXE%" goto :fail_signing_script
set "SIGNTOOL="
for /f "delims=" %%S in ('where.exe signtool.exe 2^>nul') do if not defined SIGNTOOL if exist "%%~S" set "SIGNTOOL=%%~S"
if not defined SIGNTOOL for /d %%V in ("%ProgramFiles(x86)%\Windows Kits\10\bin\10.*") do if exist "%%~V\x64\signtool.exe" set "SIGNTOOL=%%~V\x64\signtool.exe"
if not defined SIGNTOOL if exist "%ProgramFiles%\Windows Kits\10\bin" for /d %%V in ("%ProgramFiles%\Windows Kits\10\bin\10.*") do if exist "%%~V\x64\signtool.exe" set "SIGNTOOL=%%~V\x64\signtool.exe"
if not defined SIGNTOOL goto :fail_signtool
if exist "%INSTALLER_STAGE%" rmdir /s /q "%INSTALLER_STAGE%"
if exist "%INSTALLER_STAGE%" goto :fail_stage
mkdir "%INSTALLER_STAGE%"
if errorlevel 1 goto :fail_stage
set "FAILED_STAGE=PAYLOAD_BUILD"
>> "%RESULT%" echo STAGE=PAYLOAD_BUILD
call "%ROOT%\BUILD_RELEASE_ONEDIR.cmd" --company --no-pause --installer-payload > "%LOG_DIR%\R15_payload_build.log" 2>&1
if errorlevel 1 goto :fail_payload
findstr /X /C:"STATUS=SUCCESS" "%LOG_DIR%\R14_BUILD_RESULT.txt" >nul 2>&1
if errorlevel 1 goto :fail_payload
for %%F in ("%APP_DIR%\Sunrise.FileManager.exe" "%APP_DIR%\Sunrise.FileManager.dll" "%APP_DIR%\hostfxr.dll" "%APP_DIR%\coreclr.dll") do if not exist "%%~F" goto :fail_payload
if not exist "%CERT_FILE%" goto :fail_payload
set "CERT_THUMBPRINT="
for /f "usebackq tokens=1,2 delims==" %%A in ("%CERT_FILE%") do if /I "%%A"=="THUMBPRINT" set "CERT_THUMBPRINT=%%B"
if not defined CERT_THUMBPRINT goto :fail_payload
set "DOTNET_EXE="
for %%D in ("%ProgramW6432%\dotnet\dotnet.exe" "%ProgramFiles%\dotnet\dotnet.exe" "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" "%USERPROFILE%\.dotnet\dotnet.exe") do if not defined DOTNET_EXE if exist "%%~D" set "DOTNET_EXE=%%~D"
if not defined DOTNET_EXE goto :fail_payload
set "FAILED_STAGE=INSTALLER_COMPILE"
>> "%RESULT%" echo STAGE=INSTALLER_COMPILE
set "SUNRISE_INSTALLER_PAYLOAD=%APP_DIR%"
set "SUNRISE_INSTALLER_OUTPUT=%INSTALLER_STAGE%"
set "SUNRISE_INSTALLER_VERSION=%SFM_APP_VERSION%"
"%ISCC%" "/SSunriseGroup=$q%SIGNTOOL%$q sign /fd SHA256 /sha1 %CERT_THUMBPRINT% /s My /tr %TIMESTAMP_URL% /td SHA256 $f" "%ISS%" > "%LOG_DIR%\R15_inno_compile.log" 2>&1
if errorlevel 1 goto :fail_compile
if not exist "%UNSIGNED_SETUP%" goto :fail_compile
for %%F in ("%UNSIGNED_SETUP%") do if %%~zF LSS 1000000 goto :fail_compile
REM ISCC has already signed both Setup and the embedded Uninstaller.
set "FAILED_STAGE=INSTALLER_VERIFY"
>> "%RESULT%" echo STAGE=INSTALLER_VERIFY
"%POWERSHELL_EXE%" -NoLogo -NoProfile -NonInteractive -File "%SIGNING_SCRIPT%" -Action VerifyEmbedded -Path "%UNSIGNED_SETUP%" -Thumbprint "%CERT_THUMBPRINT%" > "%LOG_DIR%\R15_verify_setup.log" 2>&1
if errorlevel 1 goto :fail_sign
"%SIGNTOOL%" verify /pa /v "%UNSIGNED_SETUP%" > "%LOG_DIR%\R15_wintrust_setup.log" 2>&1
if errorlevel 1 (
 findstr /I /C:"0x800B0109" /C:"0x800B010A" /C:"not trusted" /C:"untrusted root" "%LOG_DIR%\R15_wintrust_setup.log" >nul 2>&1
 if errorlevel 1 goto :fail_sign
 >> "%RESULT%" echo WINTRUST=UNTRUSTED_SELF_SIGNED
) else (
 >> "%RESULT%" echo WINTRUST=PASS
)
set "FAILED_STAGE=COMMIT"
>> "%RESULT%" echo STAGE=COMMIT
if exist "%BACKUP_SETUP%" del /q /f "%BACKUP_SETUP%"
if exist "%FINAL_SETUP%" (
 move /y "%FINAL_SETUP%" "%BACKUP_SETUP%" >nul
 if errorlevel 1 goto :fail_commit
)
move /y "%UNSIGNED_SETUP%" "%FINAL_SETUP%" >nul
if errorlevel 1 goto :fail_restore
if not exist "%FINAL_SETUP%" goto :fail_restore
certutil -hashfile "%FINAL_SETUP%" SHA256 > "%LOG_DIR%\R15_SIGNED_SETUP_SHA256.txt" 2>&1
if errorlevel 1 goto :fail_restore
if exist "%BACKUP_SETUP%" del /q /f "%BACKUP_SETUP%"
if exist "%INSTALLER_STAGE%" rmdir /s /q "%INSTALLER_STAGE%"
if exist "%DIST%\_R18_installer_payload" rmdir /s /q "%DIST%\_R18_installer_payload"
> "%RESULT%" echo STATUS=SUCCESS
>> "%RESULT%" echo STAGE=COMPLETE
>> "%RESULT%" echo FORMAT=INSTALLER_SETUP_EXE
>> "%RESULT%" echo APP_VERSION=%SFM_APP_VERSION%
>> "%RESULT%" echo GROUP_SIGNER=CN=Sunrise Group
>> "%RESULT%" echo SETUP=%FINAL_SETUP%
>> "%RESULT%" echo COMPANY_LOGIC_TESTS=SKIPPED
>> "%RESULT%" echo SIGNED_SHA256_LOG=%LOG_DIR%\R15_SIGNED_SETUP_SHA256.txt
echo [SUCCESS] Installer available:
echo "%FINAL_SETUP%"
if "%NO_PAUSE%"=="0" start "" explorer.exe /select,"%FINAL_SETUP%"
if "%NO_PAUSE%"=="0" pause
exit /b 0
REM Version check is intentionally independent of FINDSTR's regex engine and
REM Windows file line-ending style; uses built-in FOR tokenization and digit checks.
:validate_release_version
setlocal EnableExtensions DisableDelayedExpansion
set "VRAW=%~1"
set "VMAJOR="
set "VMINOR="
set "VPATCH="
set "VEXTRA="
for /f "tokens=1-4 delims=." %%A in ("%VRAW%") do (
 set "VMAJOR=%%A"
 set "VMINOR=%%B"
 set "VPATCH=%%C"
 set "VEXTRA=%%D"
)
if not defined VMAJOR (endlocal & exit /b 1)
if not defined VMINOR (endlocal & exit /b 1)
if not defined VPATCH (endlocal & exit /b 1)
if defined VEXTRA (endlocal & exit /b 1)
REM An exact reconstruction rejects leading/trailing/doubled dots.
if not "%VRAW%"=="%VMAJOR%.%VMINOR%.%VPATCH%" (endlocal & exit /b 1)
call :check_decimal_segment "%VMAJOR%"
if errorlevel 1 (endlocal & exit /b 1)
call :check_decimal_segment "%VMINOR%"
if errorlevel 1 (endlocal & exit /b 1)
call :check_decimal_segment "%VPATCH%"
if errorlevel 1 (endlocal & exit /b 1)
endlocal & exit /b 0

:check_decimal_segment
if "%~1"=="" exit /b 1
REM Digits are FOR delimiters. If ANY other character remains, reject.
for /f "delims=0123456789" %%D in ("%~1") do exit /b 1
exit /b 0

:fail_company
set "FAILED_STAGE=COMPANY_POLICY"
goto :fail
:fail_version
set "FAILED_STAGE=VERSION_OR_INSTALLER_CONFIG"
if not defined VERSION_PROBLEM set "VERSION_PROBLEM=UNKNOWN_VERSION_CONFIG_ERROR"
>> "%VERSION_DIAG%" echo CHECK=FAILED
>> "%VERSION_DIAG%" echo REASON=%VERSION_PROBLEM%
echo [ERROR] VERSION preflight failed: %VERSION_PROBLEM%
echo [ERROR] Expected installer\RELEASE_VERSION.txt as a single version such as 1.0.0.
echo [ERROR] Detailed diagnostic: build-logs\R15_version_preflight.log
goto :fail
:fail_inno
set "FAILED_STAGE=INNO_SETUP_NOT_FOUND"
echo [ERROR] Inno Setup ISCC.exe is required on the build PC. No auto-installation performed.
goto :fail
:fail_signing_script
set "FAILED_STAGE=SIGNING_SCRIPT_UNAVAILABLE"
echo [ERROR] Group-signing verification requires Windows PowerShell and the source signing script.
goto :fail
:fail_signtool
set "FAILED_STAGE=SIGNTOOL_NOT_FOUND"
goto :fail
:fail_stage
set "FAILED_STAGE=STAGING"
goto :fail
:fail_payload
set "FAILED_STAGE=PAYLOAD_BUILD"
echo [ERROR] OneDir signed payload build failed. See R15_payload_build.log and R14 logs.
goto :fail
:fail_compile
set "FAILED_STAGE=INSTALLER_COMPILE"
echo [ERROR] Inno Setup compilation failed. See R15_inno_compile.log.
goto :fail
:fail_sign
set "FAILED_STAGE=INSTALLER_SIGN_OR_VERIFY"
echo [ERROR] Setup Authenticode signing/verification failed. See R15_inno_compile.log and R15_verify_setup.log.
goto :fail
:fail_commit
set "FAILED_STAGE=COMMIT_BACKUP"
goto :fail
:fail_restore
if exist "%FINAL_SETUP%" del /q /f "%FINAL_SETUP%"
if exist "%BACKUP_SETUP%" move /y "%BACKUP_SETUP%" "%FINAL_SETUP%" >nul
set "FAILED_STAGE=COMMIT_RESTORE"
:fail
echo [ERROR] Installer build stopped at %FAILED_STAGE%.
> "%RESULT%" echo STATUS=FAILED
>> "%RESULT%" echo STAGE=%FAILED_STAGE%
if defined VERSION_PROBLEM >> "%RESULT%" echo DETAIL=%VERSION_PROBLEM%
>> "%RESULT%" echo FORMAT=INSTALLER_SETUP_EXE
>> "%RESULT%" echo COMPANY_LOGIC_TESTS=SKIPPED
if "%NO_PAUSE%"=="0" pause
exit /b 1
