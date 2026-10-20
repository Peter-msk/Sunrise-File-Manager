@echo off
REM R12 - automatic, project-pinned self-signed certificate provisioning.
REM No custom signer EXE, PowerShell or CA import. Never export private keys.
REM First official release creates a project signer in CurrentUser/My.
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
REM R12 - automatic, profile-pinned signer, no manual identity selection.
REM Many unrelated Sunrise products use the same CN. Never choose by name or date.
REM First File Manager release generates a fresh project signer, its public CER
REM survives new source folders, and all later releases use the same project PIN.
set "CERT_OUT=%CERT_PIN_DIR%\pending_group_public.cer"
set "LEGACY_OUT=%CERT_ROOT%\R14_generated_group_public.cer"
set "CERT_CREATED_NOW=0"
REM Recover the exact earlier pending CER from this source folder if present.
if exist "%LEGACY_OUT%" (
  if exist "%CERT_OUT%" (
    fc /b "%LEGACY_OUT%" "%CERT_OUT%" >nul 2>&1
    if errorlevel 1 goto :conflicting_pending
  ) else (
    copy /b /y "%LEGACY_OUT%" "%CERT_OUT%" >nul
    if errorlevel 1 goto :pin_failed
  )
)
if exist "%CERT_OUT%" (
  if "%CERT_CHECK_ONLY%"=="1" (
    >> "%CERT_SETUP_LOG%" echo STATUS=READY_TO_RECOVER_PENDING_CERTIFICATE
    echo [CERT] Existing project public certificate ready to recover automatically.
    exit /b 0
  )
  >> "%CERT_SETUP_LOG%" echo STATUS=RECOVERING_PENDING_CERTIFICATE
  goto :inspect_public_certificate
)
REM If an installed product or prior signed Setup exists without a PIN, stop.
REM A release publisher must not rotate merely because a profile PIN was lost.
if exist "%LOCALAPPDATA%\Programs\Sunrise File Manager\Sunrise.FileManager.exe" goto :existing_release_without_pin
for %%F in ("%~dp0..\dist\Sunrise_File_Manager_*_Setup.exe") do if exist "%%~fF" goto :existing_release_without_pin
if "%CERT_CHECK_ONLY%"=="1" (
  >> "%CERT_SETUP_LOG%" echo STATUS=READY_TO_CREATE_DURING_BUILD
  echo [CERT] First project signer will be generated during the official build.
  exit /b 0
)
REM New project identity only. Existing other-product CN=Sunrise Group keys
REM are untouched and never selected automatically. No private key is exported.
>> "%CERT_SETUP_LOG%" echo STATUS=CREATING_PROJECT_SELF_SIGNED_CERTIFICATE
certreq.exe -new -user -q "%~dp0Sunrise.GroupCodeSigning.inf" "%CERT_OUT%" > "%CERT_ROOT%\R14_certreq_create.log" 2>&1
if errorlevel 1 goto :certreq_failed
if not exist "%CERT_OUT%" goto :certreq_failed
set "CERT_CREATED_NOW=1"
:inspect_public_certificate
REM Verify the one public certificate before any signature selection.
certutil.exe -dump "%CERT_OUT%" > "%CERT_ROOT%\R14_certreq_dump.log" 2>&1
if errorlevel 1 goto :cert_dump_failed
findstr /I /L /C:"CN=Sunrise Group" "%CERT_ROOT%\R14_certreq_dump.log" >nul 2>&1
if errorlevel 1 goto :public_cert_mismatch
findstr /I /L /C:"1.3.6.1.5.5.7.3.3" "%CERT_ROOT%\R14_certreq_dump.log" >nul 2>&1
if errorlevel 1 goto :public_cert_mismatch
REM certreq may write Base64 or DER. certutil -decode succeeds on Base64.
REM If decoding fails, retain the original DER; the store+subject check gates pinning.
set "HASH_INPUT=%CERT_OUT%"
set "CERT_DER=%CERT_ROOT%\R14_generated_group_decoded.der"
if exist "%CERT_DER%" del /q /f "%CERT_DER%"
certutil.exe -decode "%CERT_OUT%" "%CERT_DER%" > "%CERT_ROOT%\R14_cert_decode.log" 2>&1
if not errorlevel 1 (
  if not exist "%CERT_DER%" goto :cert_decode_failed
  set "HASH_INPUT=%CERT_DER%"
)
REM -hashfile returns one digest of certificate bytes. Do not parse -dump extensions.
certutil.exe -hashfile "%HASH_INPUT%" SHA1 > "%CERT_ROOT%\R14_cert_thumbprint_sha1.log" 2>&1
if errorlevel 1 goto :hash_not_found
set "NEW_HASH="
set "NEW_HASH_COUNT=0"
for /f "usebackq delims=" %%H in ("%CERT_ROOT%\R14_cert_thumbprint_sha1.log") do call :consider_sha1 "%%H"
if not "%NEW_HASH_COUNT%"=="1" goto :hash_not_found
set "SUNRISE_CERT_SHA1=%NEW_HASH%"
REM Confirm the same fingerprint exists in CurrentUser/My with the expected CN.
call "%~dp0FindSunriseCertificate.cmd" > "%CERT_ROOT%\R14_signing_discovery.log" 2>&1
if errorlevel 1 goto :new_cert_invalid
if /I not "%SUNRISE_CERT_THUMBPRINT%"=="%NEW_HASH%" goto :new_cert_invalid
REM Pin ONLY public SHA1; never export/store private key material.
> "%CERT_PIN%.tmp" echo %NEW_HASH%
if errorlevel 1 goto :pin_failed
move /y "%CERT_PIN%.tmp" "%CERT_PIN%" >nul
if errorlevel 1 goto :pin_failed
if exist "%CERT_DER%" del /q /f "%CERT_DER%"
REM Preserve public CER in user profile for future recovery, never export key.
if "%CERT_CREATED_NOW%"=="1" (
  >> "%CERT_SETUP_LOG%" echo STATUS=CREATED_AND_PINNED
) else (
  >> "%CERT_SETUP_LOG%" echo STATUS=RECOVERED_AND_PINNED
)
>> "%CERT_SETUP_LOG%" echo THUMBPRINT=%SUNRISE_CERT_THUMBPRINT%
echo [CERT] Verified and pinned CN=Sunrise Group; no additional certificate creation is needed.
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
:public_cert_mismatch
>> "%CERT_SETUP_LOG%" echo STATUS=PENDING_CERT_IDENTITY_OR_EKU_MISMATCH
 echo [CERT ERROR] Public certificate identity or code-signing EKU did not match; no new key created.
exit /b 1
:cert_decode_failed
>> "%CERT_SETUP_LOG%" echo STATUS=PENDING_CERT_DECODE_INVALID
exit /b 1
:cert_store_probe_failed
>> "%CERT_SETUP_LOG%" echo STATUS=CERT_STORE_PROBE_INDETERMINATE
 echo [CERT ERROR] Store query failed for a reason other than NOT_FOUND; no new key generated.
exit /b 1
:existing_release_without_pin
>> "%CERT_SETUP_LOG%" echo STATUS=EXISTING_RELEASE_WITHOUT_PIN
 echo [CERT ERROR] Existing product release but signer pin is missing. Do not rotate identity.
exit /b 1
:conflicting_pending
>> "%CERT_SETUP_LOG%" echo STATUS=CONFLICTING_PENDING_PUBLIC_CERTIFICATES
 echo [CERT ERROR] Different pending public certs detected; no automatic replacement.
exit /b 1
:hash_not_found
>> "%CERT_SETUP_LOG%" echo STATUS=CREATED_SHA1_NOT_PARSED
>> "%CERT_SETUP_LOG%" echo HASH_COUNT=%NEW_HASH_COUNT%
echo [CERT ERROR] Certificate thumbprint not unambiguously parsed; pending public certificate preserved for safe recovery.
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
