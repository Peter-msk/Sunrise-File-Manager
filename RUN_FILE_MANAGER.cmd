@echo off
REM R15 developer convenience. Starts the INSTALLED program, never a stale OneDir build.
set "EXE=%LOCALAPPDATA%\Programs\Sunrise File Manager\Sunrise.FileManager.exe"
if not exist "%EXE%" (
 echo [NOT INSTALLED] Sunrise File Manager is not installed in the default user location.
 echo Please run the signed installer inside dist\, or launch from Start Menu after custom installation.
 pause
 exit /b 1
)
start "" "%EXE%"
exit /b 0
