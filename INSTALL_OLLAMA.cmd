@echo off
setlocal EnableExtensions
title Sunrise File Manager - Onboard AI (Ollama) Installer [CMD Only]

set "MODEL_NAME=%~1"
if "%MODEL_NAME%"=="" set "MODEL_NAME=qwen2.5:1.5b"

echo ============================================================================
echo  Sunrise File Manager - Onboard AI (Ollama) Auto-Installer
echo  Official GitHub Source: https://github.com/ollama/ollama
echo  Target Model: %MODEL_NAME%
echo  Shell: Windows CMD (cmd.exe only)
echo ============================================================================
echo.

set "OLLAMA_EXE="
where.exe ollama.exe >nul 2>&1
if not errorlevel 1 (
    for /f "delims=" %%I in ('where.exe ollama.exe') do set "OLLAMA_EXE=%%I"
)
if exist "%LOCALAPPDATA%\Programs\Ollama\ollama.exe" (
    set "OLLAMA_EXE=%LOCALAPPDATA%\Programs\Ollama\ollama.exe"
)

if defined OLLAMA_EXE goto :have_ollama

echo [1/3] Downloading official OllamaSetup.exe from GitHub Releases...
set "SETUP_FILE=%TEMP%\OllamaSetup.exe"
if exist "%SETUP_FILE%" del /f /q "%SETUP_FILE%"
curl.exe -L --fail --retry 3 -o "%SETUP_FILE%" "https://github.com/ollama/ollama/releases/latest/download/OllamaSetup.exe"
if errorlevel 1 goto :download_fail

echo [2/3] Running Ollama installer...
"%SETUP_FILE%" /SILENT /NORESTART
if exist "%LOCALAPPDATA%\Programs\Ollama\ollama.exe" (
    set "OLLAMA_EXE=%LOCALAPPDATA%\Programs\Ollama\ollama.exe"
) else (
    where.exe ollama.exe >nul 2>&1
    if not errorlevel 1 (
        for /f "delims=" %%I in ('where.exe ollama.exe') do set "OLLAMA_EXE=%%I"
    )
)

if not defined OLLAMA_EXE goto :install_fail

:have_ollama
echo [INFO] Found Ollama executable: "%OLLAMA_EXE%"
echo [3/3] Starting local Ollama server and pulling model (%MODEL_NAME%)...
start "" /B "%OLLAMA_EXE%" serve >nul 2>&1
timeout /t 3 /nobreak >nul
"%OLLAMA_EXE%" pull %MODEL_NAME%
if errorlevel 1 goto :pull_fail

echo.
echo ============================================================================
echo  [SUCCESS] Onboard AI (Ollama) and model (%MODEL_NAME%) are installed!
echo  You can now return to Sunrise File Manager and click [Re-check Status].
echo ============================================================================
pause
exit /b 0

:download_fail
echo.
echo [ERROR] Failed to download OllamaSetup.exe from GitHub.
echo         Please check your internet connection or download from:
echo         https://github.com/ollama/ollama/releases/latest
pause
exit /b 1

:install_fail
echo.
echo [ERROR] Ollama installation did not complete or was canceled.
pause
exit /b 1

:pull_fail
echo.
echo [WARNING] Ollama is installed, but pulling model %MODEL_NAME% failed.
echo           You can run: "%OLLAMA_EXE%" pull %MODEL_NAME%
pause
exit /b 1
