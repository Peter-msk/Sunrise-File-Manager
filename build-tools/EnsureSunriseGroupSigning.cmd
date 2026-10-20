@echo off
REM R07 - build-integrated group certificate provisioning using Windows certreq.exe.
REM No custom signer EXE, PowerShell or CA import. Never export private keys.
REM First official build creates one self-signed group certificate in CurrentUser/My.
REM Later builds require the same pinned certificate; do NOT silently rotate it.
REM --check-only: inspect prerequisites or the pinned signer; NO certificate creation.
set "CERT_SETUP_LOG=%~dp0..\build-logs\R14_cert_provision.log"
set "CERT_ROOT=%~dp0..\build-logs"
if not exist "%CERT_ROOT%" mkdir "%CERT_ROOT%"
if not exist "%CERT_ROOT%" exit /b 1
> "%CERT_SETUP_LOG%" echo STAGE=CERTIFICATE_PROVISION
set "CERT_CHECK_ONLY=0"
if /I "%~1"=="--check-only" set "CERT_CHECK_ONLY=1"
REM Never let a previous CAPTURE profile or external fingerprint change this publisher.
if defined SUNRISE_CERT_PROFILE if /I not "%SUNRISE_CERT_PROFILE%"=="GROUP" goto :foreign_profile
if defined SUNRISE_CERT_SHA1 goto :external_override
set "SUNRISE_CERT_PROFILE=GROUP"
REM Ignore deprecated legacy manual-creation switch; this pipeline owns creation.
set "SUNRISE_ALLOW_CERT_CREATE="
set "SUNRISE_CERT_SUBJECT_CN=Sunrise Group"
set "SUNRISE_CERT_THUMBPRINT="
if not defined LOCALAPPDATA goto :no_appdata
set "CERT_PIN_DIR=%LOCALAPPDATA%\SunriseGroup\FileManager\Signing"
set "CERT_PIN=%CERT_PIN_DIR%\group_signer.sha1"
if not exist "%CERT_PIN_DIR%" mkdir "%CERT_PIN_DIR%"
if not exist "%CERT_PIN_DIR%" goto :pin_folder_failed
where.exe certutil.exe >nul 2>&1
if errorlevel 1 goto :missing_tools
if not exist "%~dp0FindSunriseCertificate.cmd" goto :missing_tools
if exist "%CERT_PIN%" goto :pinned_certificate
if not exist "%~dp0Sunrise.GroupCodeSigning.inf" goto :missing_tools
where.exe certreq.exe >nul 2>&1
if errorlevel 1 goto :missing_tools
if "%CERT_CHECK_ONLY%"=="1" (
  >> "%CERT_SETUP_LOG%" echo STATUS=READY_TO_CREATE_DURING_BUILD
  echo [CERT] No project pin yet. First official build will create CN=Sunrise Group using certreq.exe.
  exit /b 0
)
REM Do not load unrelated existing certificates under the same friendly name.
REM One pinned signer is created here, then used for app, DLL, Setup and updates.
set "CERT_OUT=%CERT_ROOT%\R14_generated_group_public.cer"
if exist "%CERT_OUT%" del /q /f "%CERT_OUT%"
>> "%CERT_SETUP_LOG%" echo STATUS=CREATING_SELF_SIGNED_CERTIFICATE
certreq.exe -new -user -q "%~dp0Sunrise.GroupCodeSigning.inf" "%CERT_OUT%" > "%CERT_ROOT%\R14_certreq_create.log" 2>&1
if errorlevel 1 goto :certreq_failed
if not exist "%CERT_OUT%" goto :certreq_failed
certutil.exe -dump "%CERT_OUT%" > "%CERT_ROOT%\R14_certreq_dump.log" 2>&1
if errorlevel 1 goto :cert_dump_failed
findstr /I /C:"sha1" "%CERT_ROOT%\R14_certreq_dump.log" > "%CERT_ROOT%\R14_created_sha1_lines.log" 2>nul
if errorlevel 1 goto :hash_not_found
set "NEW_HASH="
set "NEW_HASH_COUNT=0"
for /f "usebackq tokens=1,* delims=:" %%A in ("%CERT_ROOT%\R14_created_sha1_lines.log") do call :consider_sha1 "%%B"
if not "%NEW_HASH_COUNT%"=="1" goto :hash_not_found
set "SUNRISE_CERT_SHA1=%NEW_HASH%"
call "%~dp0FindSunriseCertificate.cmd" > "%CERT_ROOT%\R14_signing_discovery.log" 2>&1
if errorlevel 1 goto :new_cert_invalid
if /I not "%SUNRISE_CERT_THUMBPRINT%"=="%NEW_HASH%" goto :new_cert_invalid
REM Pin ONLY a public SHA1 thumbprint, not a PFX, password or private key.
> "%CERT_PIN%.tmp" echo %NEW_HASH%
if errorlevel 1 goto :pin_failed
move /y "%CERT_PIN%.tmp" "%CERT_PIN%" >nul
if errorlevel 1 goto :pin_failed
if exist "%CERT_OUT%" del /q /f "%CERT_OUT%"
>> "%CERT_SETUP_LOG%" echo STATUS=CREATED_AND_PINNED
>> "%CERT_SETUP_LOG%" echo THUMBPRINT=%SUNRISE_CERT_THUMBPRINT%
echo [CERT] Generated and pinned a build-local CN=Sunrise Group self-signed certificate.
exit /b 0
:pinned_certificate
REM Validate file characters BEFORE FOR expansion to avoid command injection from a modified pin.
findstr /I /R /V /C:"^[0-9A-F][0-9A-F]*$" "%CERT_PIN%" >nul 2>&1
if not errorlevel 1 goto :invalid_pin
set "PINNED_SHA1="
for /f "usebackq tokens=* delims=" %%H in ("%CERT_PIN%") do if not defined PINNED_SHA1 set "PINNED_SHA1=%%H"
call :validate_sha1 "%PINNED_SHA1%"
if errorlevel 1 goto :invalid_pin
set "SUNRISE_CERT_SHA1=%PINNED_SHA1%"
call "%~dp0FindSunriseCertificate.cmd" > "%CERT_ROOT%\R14_signing_discovery.log" 2>&1
if errorlevel 1 goto :pinned_cert_unavailable
if /I not "%SUNRISE_CERT_THUMBPRINT%"=="%PINNED_SHA1%" goto :pinned_cert_unavailable
>> "%CERT_SETUP_LOG%" echo STATUS=REUSED_PINNED
>> "%CERT_SETUP_LOG%" echo THUMBPRINT=%SUNRISE_CERT_THUMBPRINT%
echo [CERT] Using project-generated pinned group certificate; no regeneration.
exit /b 0
:consider_sha1
setlocal EnableDelayedExpansion
set "HEX=%~1"
set "HEX=!HEX: =!"
set "HEX=!HEX:-=!"
if "!HEX:~39,1!"=="" (endlocal & exit /b 0)
if not "!HEX:~40,1!"=="" (endlocal & exit /b 0)
echo(!HEX!| findstr /I /R /C:"[^0123456789ABCDEF]" >nul
if not errorlevel 1 (endlocal & exit /b 0)
endlocal & set /a NEW_HASH_COUNT+=1 >nul & set "NEW_HASH=%HEX%"
exit /b 0
:validate_sha1
setlocal EnableDelayedExpansion
set "HEX=%~1"
if "!HEX:~39,1!"=="" (endlocal & exit /b 1)
if not "!HEX:~40,1!"=="" (endlocal & exit /b 1)
echo(!HEX!| findstr /I /R /C:"[^0123456789ABCDEF]" >nul
if not errorlevel 1 (endlocal & exit /b 1)
endlocal & exit /b 0
:foreign_profile
>> "%CERT_SETUP_LOG%" echo STATUS=INVALID_PROFILE
 echo [CERT ERROR] R07 official build only supports CN=Sunrise Group; remove CAPTURE profile.
exit /b 1
:external_override
>> "%CERT_SETUP_LOG%" echo STATUS=EXTERNAL_SHA1_REJECTED
 echo [CERT ERROR] R07 uses a project-created pinned certificate. Clear SUNRISE_CERT_SHA1.
exit /b 1
:no_appdata
>> "%CERT_SETUP_LOG%" echo STATUS=LOCALAPPDATA_UNAVAILABLE
exit /b 1
:pin_folder_failed
>> "%CERT_SETUP_LOG%" echo STATUS=PIN_FOLDER_UNWRITABLE
exit /b 1
:missing_tools
>> "%CERT_SETUP_LOG%" echo STATUS=NATIVE_TOOLS_OR_INF_MISSING
 echo [CERT ERROR] certreq.exe, certutil.exe, INF template and resolver are required.
exit /b 1
:certreq_failed
>> "%CERT_SETUP_LOG%" echo STATUS=CERTREQ_CREATION_FAILED
 echo [CERT ERROR] certreq certificate generation failed. See R14_certreq_create.log.
exit /b 1
:cert_dump_failed
>> "%CERT_SETUP_LOG%" echo STATUS=CERTREQ_CERT_DUMP_FAILED
exit /b 1
:hash_not_found
>> "%CERT_SETUP_LOG%" echo STATUS=CREATED_SHA1_NOT_PARSED
>> "%CERT_SETUP_LOG%" echo HASH_COUNT=%NEW_HASH_COUNT%
echo [CERT ERROR] Self-signed cert created but the new thumbprint could not be parsed.
exit /b 1
:new_cert_invalid
>> "%CERT_SETUP_LOG%" echo STATUS=CREATED_CERT_STORE_VERIFY_FAILED
 echo [CERT ERROR] New certificate was not selectable from CurrentUser/My.
exit /b 1
:pin_failed
>> "%CERT_SETUP_LOG%" echo STATUS=PIN_WRITE_FAILED
 echo [CERT ERROR] Cannot persist stable signing identity for future updates.
exit /b 1
:invalid_pin
>> "%CERT_SETUP_LOG%" echo STATUS=INVALID_PINNED_SHA1
 echo [CERT ERROR] Project signing pin corrupt. Do not regenerate automatically.
exit /b 1
:pinned_cert_unavailable
>> "%CERT_SETUP_LOG%" echo STATUS=PINNED_CERT_MISSING_OR_INVALID
 echo [CERT ERROR] Pinned private signing certificate missing or invalid; do not rotate identity.
echo [CERT ERROR] Contact IT; do not remove the pin to force a new publisher.
exit /b 1
