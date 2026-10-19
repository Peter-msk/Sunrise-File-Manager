@echo off
setlocal EnableExtensions
cd /d "%~dp0"
echo ============================================================================
echo Sunrise File Manager - .NET 10 SDK diagnostic (no installation)
echo ============================================================================
for %%D in (
    "%ProgramW6432%\dotnet\dotnet.exe"
    "%ProgramFiles%\dotnet\dotnet.exe"
    "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
    "%USERPROFILE%\.dotnet\dotnet.exe"
) do if exist "%%~D" (
    echo [HOST] %%~D
    "%%~D" --list-sdks
)
echo [PATH]
where.exe dotnet.exe 2>nul
echo [PROJECT SDK POLICY]
type "%~dp0source\global.json"
echo.
pause
exit /b 0
