@echo off
REM Sunrise Capture-compatible certificate discovery. Windows certutil + SignTool only.
REM Runs no PowerShell, custom signing EXE, certreq, or certificate auto-provisioning.
REM Requires exactly one matching existing CN=Sunrise Group cert or an explicit SHA1.
REM Returns SUNRISE_CERT_THUMBPRINT in the caller environment; never exports keys.
set "SUNRISE_CERT_THUMBPRINT="
set "CERT_MATCH_COUNT=0"
set "CERT_ROOT=%~dp0..\build-logs"
if not exist "%CERT_ROOT%" mkdir "%CERT_ROOT%"
if not exist "%CERT_ROOT%" exit /b 1
set "CERT_STORE_LOG=%CERT_ROOT%\R14_certutil_store.log"
set "CERT_HASH_LINES=%CERT_ROOT%\R14_certutil_sha1_lines.log"
set "CERT_DIAG=%CERT_ROOT%\R14_signing_preflight.log"
> "%CERT_DIAG%" echo SIGNING_DISCOVERY=START
if exist "%CERT_ROOT%\R14_signing_certificate.txt" del /q /f "%CERT_ROOT%\R14_signing_certificate.txt"
where.exe certutil.exe >nul 2>&1
if errorlevel 1 (
 >> "%CERT_DIAG%" echo STATUS=CERTUTIL_MISSING
 echo [SIGNING ERROR] Windows certutil.exe not available.
 exit /b 1
)
if "%SUNRISE_ALLOW_CERT_CREATE%"=="1" (
 >> "%CERT_DIAG%" echo STATUS=CERT_CREATION_DISABLED
 echo [SIGNING ERROR] Certificate auto-generation is not supported. Contact IT.
 exit /b 1
)
REM Optional explicit thumbprint avoids ambiguity when several certificates share a name.
if defined SUNRISE_CERT_SHA1 (
  call :is_sha1 "%SUNRISE_CERT_SHA1%"
  if errorlevel 1 (
    >> "%CERT_DIAG%" echo STATUS=INVALID_EXPLICIT_SHA1
    echo [SIGNING ERROR] SUNRISE_CERT_SHA1 must be exactly 40 hexadecimal characters.
    exit /b 1
  )
  set "SUNRISE_CERT_THUMBPRINT=%SUNRISE_CERT_SHA1%"
) else (
  certutil.exe -user -store My "Sunrise Group" > "%CERT_STORE_LOG%" 2>&1
  if errorlevel 1 goto :no_cert
  REM Localized 'Cert Hash(sha1)' contains sha1; only parse the right side of colon.
  REM This avoids the Capture R06 bug where literal 'sha1' contributed 'a1' to hash.
  findstr /I /C:"sha1" "%CERT_STORE_LOG%" > "%CERT_HASH_LINES%" 2>nul
  if errorlevel 1 goto :no_cert
  for /f "usebackq tokens=1,* delims=:" %%A in ("%CERT_HASH_LINES%") do call :consider_hash "%%B"
)
if not defined SUNRISE_CERT_SHA1 if not "%CERT_MATCH_COUNT%"=="1" goto :ambiguous_cert
REM Pin the selected certificate to CurrentUser/My before any signing begins.
certutil.exe -user -store My "%SUNRISE_CERT_THUMBPRINT%" > "%CERT_STORE_LOG%" 2>&1
if errorlevel 1 goto :no_cert
findstr /I /L /C:"CN=Sunrise Group" "%CERT_STORE_LOG%" >nul 2>&1
if errorlevel 1 goto :wrong_subject
REM No PFX export. SignTool enforces private key availability and code-signing EKU.
> "%CERT_ROOT%\R14_signing_certificate.txt" echo THUMBPRINT=%SUNRISE_CERT_THUMBPRINT%
>> "%CERT_ROOT%\R14_signing_certificate.txt" echo SUBJECT=CN=Sunrise Group
>> "%CERT_DIAG%" echo STATUS=SELECTED
>> "%CERT_DIAG%" echo SUBJECT=CN=Sunrise Group
>> "%CERT_DIAG%" echo THUMBPRINT=%SUNRISE_CERT_THUMBPRINT%
echo [SIGNING] Using existing CN=Sunrise Group CurrentUser/My certificate (%SUNRISE_CERT_THUMBPRINT%).
exit /b 0
:no_cert
>> "%CERT_DIAG%" echo STATUS=CERT_NOT_FOUND
 echo [SIGNING ERROR] Existing Sunrise Group certificate not found in CurrentUser/My.
 echo [SIGNING ERROR] Check R14_certutil_store.log; request IT-provisioned signing certificate.
exit /b 1
:ambiguous_cert
>> "%CERT_DIAG%" echo STATUS=NO_OR_MULTIPLE_CERTIFICATES
>> "%CERT_DIAG%" echo MATCH_COUNT=%CERT_MATCH_COUNT%
 echo [SIGNING ERROR] Found %CERT_MATCH_COUNT% matching SHA1 records; require exactly one.
 echo [SIGNING ERROR] If several certificates exist, set SUNRISE_CERT_SHA1 to the authorized 40-digit thumbprint.
exit /b 1
:wrong_subject
>> "%CERT_DIAG%" echo STATUS=SUBJECT_MISMATCH
 echo [SIGNING ERROR] Selected certificate not identified as CN=Sunrise Group.
exit /b 1
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
