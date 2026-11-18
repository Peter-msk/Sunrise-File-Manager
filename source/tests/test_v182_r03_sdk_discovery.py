"""Build source contract; execution of cmd.exe on Windows is a separate gate."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = (ROOT / 'build-tools/FindDotNet10.cmd').read_text(encoding='ascii')


def test_explicit_override_precedes_automatic_candidates_and_fails_closed():
    block = SCRIPT.split('if defined SUNRISE_DOTNET (', 1)[1].split('REM Custom installation', 1)[0]
    assert 'call :root "%SUNRISE_DOTNET%"' in block
    assert 'if defined SUNRISE_DOTNET_RESOLVED goto :success' in block
    assert 'RESULT=INVALID_EXPLICIT_OVERRIDE' in block and 'exit /b 1' in block


def test_discovery_includes_custom_registered_and_user_roots():
    for token in ('DOTNET_ROOT_X64', 'DOTNET_ROOT', 'DOTNET_INSTALL_DIR',
                  r'HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64',
                  r'HKCU\SOFTWARE\dotnet\Setup\InstalledVersions\x64',
                  '/reg:64', '/reg:32', r'%USERPROFILE%\.dotnet', 'where.exe dotnet.exe'):
        assert token in SCRIPT


def test_candidate_is_deduplicated_before_invocation_and_logs_each_failure():
    candidate = SCRIPT.split('\n:candidate\n', 1)[1]
    assert candidate.index('if /I "%%~B"=="%~f1" exit /b 0') < candidate.index('"%~f1" --version')
    assert '>> "%SDK_PROBE_FILE%" type "%SDK_VERSION_FILE%"' in candidate
    assert '"%~f1" --list-sdks >> "%SDK_PROBE_FILE%"' in candidate


def test_selection_requires_success_and_stable_dotnet10():
    candidate = SCRIPT.split('\n:candidate\n', 1)[1]
    assert candidate.index('if not "%SDK_CANDIDATE_RC%"=="0" goto :candidate_failed') < candidate.index('set "SUNRISE_DOTNET_RESOLVED=%~f1"')
    assert r'/X /C:"10\.0\.[0-9][0-9]*"' in candidate
    assert json.loads((ROOT / 'global.json').read_text())['sdk'] == {
        'version': '10.0.100', 'rollForward': 'latestFeature', 'allowPrerelease': False}


def test_build_diagnostic_uses_actual_shared_resolver_without_installing():
    launch = (ROOT / 'SDK_DIAGNOSTICS.cmd').read_text(encoding='ascii')
    assert r'call "%~dp0build-tools\FindDotNet10.cmd"' in launch
    assert 'exit /b %RC%' in launch
    assert 'winget' not in SCRIPT.lower()
    assert 'dotnet-install' not in SCRIPT.lower()
    assert (ROOT / 'SDK_DIAGNOSTICS.ps1').is_file()


def test_official_builds_retain_shared_resolver_and_release_version():
    for name in ('BUILD_RELEASE_ONEDIR.cmd', 'BUILD_RELEASE_INSTALLER.cmd', 'BUILD_DEV_EXE.cmd', 'CHECK_INSTALLER_BUILD_ENV.cmd'):
        text = (ROOT / 'build-tools/internal' / name).read_text()
        assert r'build-tools\FindDotNet10.cmd' in text
    assert (ROOT / 'installer/RELEASE_VERSION.txt').read_text().strip() == '1.8.2'
