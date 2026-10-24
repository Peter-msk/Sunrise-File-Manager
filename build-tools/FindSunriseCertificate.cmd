@echo off
REM R06: Native certutil + SignTool certificate preflight; no PowerShell/helper EXE.
REM Default signer: CN=Sunrise Group. CAPTURE requires explicit profile opt-in.
REM Reuses existing CurrentUser/My certificates. Never creates, exports, or installs keys.
set "SUNRISE_CERT_THUMBPRINT="
set "SUNRISE_CERT_SUBJECT_CN="
set "CERT_MATCH_COUNT=0"
set "CERT_ROOT=%~dp0..\build-logs"
if not exist "%CERT_ROOT%" mkdir "%CERT_ROOT%"
if not exist "%CERT_ROOT%" exit /b 1
set "CERT_STORE_LOG=%CERT_ROOT%\R14_certutil_store.log"
set "CERT_HASH_LINES=%CERT_ROOT%\R14_certutil_sha1_lines.log"
set "CERT_DIAG=%CERT_ROOT%\R14_signing_preflight.log"
set "CERT_FILE=%CERT_ROOT%\R14_signing_certificate.txt"
> "%CERT_DIAG%" echo SIGNING_DISCOVERY=START
if exist "%CERT_FILE%" del /q /f "%CERT_FILE%"
if exist "%CERT_HASH_LINES%" del /q /f "%CERT_HASH_LINES%"
if exist "%CERT_STORE_LOG%" del /q /f "%CERT_STORE_LOG%"
where.exe certutil.exe >nul 2>&1
if errorlevel 1 goto :certutil_missing
if "%SUNRISE_ALLOW_CERT_CREATE%"=="1" goto :creation_disabled
if not defined SUNRISE_CERT_PROFILE set "SUNRISE_CERT_PROFILE=GROUP"
if /I "%SUNRISE_CERT_PROFILE%"=="GROUP" set "SUNRISE_CERT_SUBJECT_CN=Sunrise Group"
if /I "%SUNRISE_CERT_PROFILE%"=="CAPTURE" set "SUNRISE_CERT_SUBJECT_CN=Sunrise Capture Self-Signed Code Signing"
if not defined SUNRISE_CERT_SUBJECT_CN goto :invalid_profile
>> "%CERT_DIAG%" echo PROFILE=%SUNRISE_CERT_PROFILE%
>> "%CERT_DIAG%" echo EXPECTED_SUBJECT=CN=%SUNRISE_CERT_SUBJECT_CN%
if defined SUNRISE_CERT_SHA1 goto :explicit_thumbprint
REM Search only the requested identity; do not silently choose a different publisher.
certutil.exe -user -store My "%SUNRISE_CERT_SUBJECT_CN%" > "%CERT_STORE_LOG%" 2>&1
if errorlevel 1 goto :no_cert
REM Keep sha1 label out of hexadecimal extraction. The label itself contains 'a1'.
findstr /I /C:"sha1" "%CERT_STORE_LOG%" > "%CERT_HASH_LINES%" 2>nul
if errorlevel 1 goto :missing_sha1
for /f "usebackq tokens=1,* delims=:" %%A in ("%CERT_HASH_LINES%") do call :consider_hash "%%B"
if "%CERT_MATCH_COUNT%"=="0" goto :missing_sha1
if not "%CERT_MATCH_COUNT%"=="1" goto :ambiguous_cert
goto :verify_selected
:explicit_thumbprint
call :is_sha1 "%SUNRISE_CERT_SHA1%"
if errorlevel 1 goto :invalid_explicit_sha1
set "SUNRISE_CERT_THUMBPRINT=%SUNRISE_CERT_SHA1%"
>> "%CERT_DIAG%" echo SELECTION=EXPLICIT_PINNED_SHA1
:verify_selected
REM Reopen the selected certificate by SHA1 and check expected identity before signing.
certutil.exe -user -store My "%SUNRISE_CERT_THUMBPRINT%" > "%CERT_STORE_LOG%" 2>&1
if errorlevel 1 goto :no_cert
findstr /I /L /C:"CN=%SUNRISE_CERT_SUBJECT_CN%" "%CERT_STORE_LOG%" >nul 2>&1
if errorlevel 1 goto :wrong_subject
REM SignTool will additionally enforce both the selected SHA1 and expected Subject.
> "%CERT_FILE%" echo THUMBPRINT=%SUNRISE_CERT_THUMBPRINT%
>> "%CERT_FILE%" echo SUBJECT=CN=%SUNRISE_CERT_SUBJECT_CN%
>> "%CERT_FILE%" echo PROFILE=%SUNRISE_CERT_PROFILE%
>> "%CERT_DIAG%" echo STATUS=SELECTED
>> "%CERT_DIAG%" echo SUBJECT=CN=%SUNRISE_CERT_SUBJECT_CN%
>> "%CERT_DIAG%" echo THUMBPRINT=%SUNRISE_CERT_THUMBPRINT%
echo [SIGNING] Existing CurrentUser/My certificate selected: CN=%SUNRISE_CERT_SUBJECT_CN%.
exit /b 0
:certutil_missing
>> "%CERT_DIAG%" echo STATUS=CERTUTIL_MISSING
echo [SIGNING ERROR] Windows certutil.exe was not found.
exit /b 1
:creation_disabled
>> "%CERT_DIAG%" echo STATUS=CERT_CREATION_DISABLED
echo [SIGNING ERROR] Automatic certificate creation is disabled.
exit /b 1
:invalid_profile
>> "%CERT_DIAG%" echo STATUS=INVALID_CERT_PROFILE
echo [SIGNING ERROR] SUNRISE_CERT_PROFILE must be GROUP or CAPTURE.
exit /b 1
:invalid_explicit_sha1
>> "%CERT_DIAG%" echo STATUS=INVALID_EXPLICIT_SHA1
echo [SIGNING ERROR] SUNRISE_CERT_SHA1 must contain exactly 40 hexadecimal characters.
exit /b 1
:no_cert
>> "%CERT_DIAG%" echo STATUS=CERT_NOT_FOUND
>> "%CERT_DIAG%" echo EXPECTED_SUBJECT=CN=%SUNRISE_CERT_SUBJECT_CN%
echo [SIGNING ERROR] Requested certificate is not available in CurrentUser/My.
echo [SIGNING ERROR] Expected CN=%SUNRISE_CERT_SUBJECT_CN%.
if /I "%SUNRISE_CERT_PROFILE%"=="GROUP" call :capture_hint
exit /b 1
:missing_sha1
>> "%CERT_DIAG%" echo STATUS=SHA1_NOT_PARSED
>> "%CERT_DIAG%" echo EXPECTED_SUBJECT=CN=%SUNRISE_CERT_SUBJECT_CN%
echo [SIGNING ERROR] Certificate output was found, but no valid SHA1 thumbprint could be parsed.
echo [SIGNING ERROR] See build-logs\R14_certutil_sha1_lines.log and R14_certutil_store.log.
exit /b 1
:ambiguous_cert
>> "%CERT_DIAG%" echo STATUS=MULTIPLE_CERTIFICATES
>> "%CERT_DIAG%" echo MATCH_COUNT=%CERT_MATCH_COUNT%
echo [SIGNING ERROR] Multiple certificates matched the requested identity.
echo [SIGNING ERROR] Set SUNRISE_CERT_SHA1 to an IT-approved 40-character fingerprint.
exit /b 1
:wrong_subject
>> "%CERT_DIAG%" echo STATUS=SUBJECT_MISMATCH
>> "%CERT_DIAG%" echo EXPECTED_SUBJECT=CN=%SUNRISE_CERT_SUBJECT_CN%
echo [SIGNING ERROR] Pinned certificate does not match the requested publisher.
exit /b 1
:capture_hint
REM Inform only; CAPTURE is never selected automatically on a GROUP build.
certutil.exe -user -store My "Sunrise Capture Self-Signed Code Signing" > "%CERT_ROOT%\R14_capture_certificate_hint.log" 2>&1
if errorlevel 1 (
  >> "%CERT_DIAG%" echo CAPTURE_CERT_AVAILABLE=UNKNOWN_OR_NOT_FOUND
  exit /b 0
)
findstr /I /C:"sha1" "%CERT_ROOT%\R14_capture_certificate_hint.log" >nul 2>&1
if errorlevel 1 (
  >> "%CERT_DIAG%" echo CAPTURE_CERT_AVAILABLE=UNKNOWN_OR_NOT_FOUND
) else (
  >> "%CERT_DIAG%" echo CAPTURE_CERT_AVAILABLE=POSSIBLE
  echo [SIGNING] A Sunrise Capture certificate may exist, but was NOT used.
  echo [SIGNING] A different publisher requires explicit SUNRISE_CERT_PROFILE=CAPTURE approval.
)
exit /b 0
:consider_hash
setlocal EnableDelayedExpansion
set "HEX=%~1"
set "HEX=!HEX: =!"
set "HEX=!HEX:-=!"
if "!HEX:~39,1!"=="" (endlocal & exit /b 0)
if not "!HEX:~40,1!"=="" (endlocal & exit /b 0)
echo(!HEX!| findstr /I /R /C:"[^0123456789ABCDEF]" >nul
if not errorlevel 1 (endlocal & exit /b 0)
endlocal & set /a CERT_MATCH_COUNT+=1 >nul & set "SUNRISE_CERT_THUMBPRINT=%HEX%"
exit /b 0
:is_sha1
setlocal EnableDelayedExpansion
set "HEX=%~1"
if "!HEX:~39,1!"=="" (endlocal & exit /b 1)
if not "!HEX:~40,1!"=="" (endlocal & exit /b 1)
echo(!HEX!| findstr /I /R /C:"[^0123456789ABCDEF]" >nul
if not errorlevel 1 (endlocal & exit /b 1)
endlocal & exit /b 0
