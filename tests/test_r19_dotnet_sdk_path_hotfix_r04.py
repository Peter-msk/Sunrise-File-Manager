"""R04 SDK resolver contracts: no customer document reads or runtime SDK execution."""
from pathlib import Path
import json
import unittest

ROOT = Path(__file__).resolve().parents[1]
SDK = (ROOT/'build-tools/FindDotNet10.cmd').read_text('ascii')
PREFLIGHT = (ROOT/'CHECK_INSTALLER_BUILD_ENV.cmd').read_text('ascii')
PAYLOAD = (ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')
INSTALLER = (ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_text('ascii')

class DotNetSdkPathHotfix(unittest.TestCase):
    def test_shared_sdk_resolver_used_in_both_critical_paths(self):
        for cmd in (PREFLIGHT, PAYLOAD):
            self.assertIn('build-tools\\FindDotNet10.cmd', cmd)
            self.assertIn('SUNRISE_DOTNET_RESOLVED', cmd)
        self.assertIn('BUILD_RELEASE_ONEDIR.cmd', INSTALLER)

    def test_never_execute_nested_quoted_dotnet_inside_for_f_subshell(self):
        for cmd in (PREFLIGHT, PAYLOAD, SDK):
            self.assertNotIn("('\\\"%~1\\\" --list-sdks", cmd)
            self.assertNotIn("('\\\"%~1\\\" --version", cmd)
        self.assertIn('"%~1" --version > "%SDK_PROBE_FILE%" 2>&1', SDK)
        self.assertIn('for /f "usebackq tokens=1" %%V in ("%SDK_MATCH_FILE%")', SDK)

    def test_actual_global_json_selection_before_reporting_pass(self):
        self.assertIn('"%~1" --version', SDK)
        self.assertIn('findstr /R /B /C:"10\\."', SDK)
        self.assertIn('SDK_PROBE=START', SDK)
        self.assertIn('SDK=%SUNRISE_DOTNET_VERSION%', SDK)
        self.assertEqual(json.loads((ROOT/'global.json').read_text()),
                         json.loads((ROOT/'source/global.json').read_text()))

    def test_spaces_and_user_overrides_are_quoted(self):
        self.assertIn('"%ProgramW6432%\\dotnet\\dotnet.exe"', SDK)
        self.assertIn('"%ProgramFiles%\\dotnet\\dotnet.exe"', SDK)
        self.assertIn('"%LOCALAPPDATA%\\Microsoft\\dotnet\\dotnet.exe"', SDK)
        self.assertIn('"%USERPROFILE%\\.dotnet\\dotnet.exe"', SDK)
        self.assertIn('call :candidate "%%~D"', SDK)
        self.assertIn('call :candidate "%SUNRISE_DOTNET%"', SDK)
        self.assertIn('RESULT=INVALID_EXPLICIT_OVERRIDE', SDK)

    def test_logs_and_errors_separate_missing_from_unusable_sdk(self):
        for term in ('R15_dotnet_probe.log','R15_dotnet_candidate.log'):
            self.assertIn(term, PREFLIGHT)
        self.assertIn('R14_dotnet_discovery.log', PAYLOAD)
        self.assertIn('if errorlevel 1 goto :fail_sdk', PAYLOAD)
        self.assertIn('if not defined SUNRISE_DOTNET_RESOLVED goto :fail_sdk', PAYLOAD)
        self.assertIn('if "%NO_PAUSE%"=="0" pause', PREFLIGHT)

    def test_company_policy_and_installer_path_retained(self):
        self.assertIn('COMPANY_SOURCE_ONLY.flag', PREFLIGHT)
        self.assertIn('COMPANY_SOURCE_ONLY.flag', INSTALLER)
        self.assertIn('ISCC%', INSTALLER)
        self.assertIn('SIGNTOOL%', INSTALLER)
        self.assertNotIn('LogicTests.csproj', PREFLIGHT+PAYLOAD+SDK)
        for forbidden in ('-ExecutionPolicy Bypass','Set-ExecutionPolicy',
                          'Add-MpPreference','Invoke-WebRequest','winget install'):
            self.assertNotIn(forbidden, SDK+PREFLIGHT+PAYLOAD)

    def test_official_batch_files_are_ascii_crlf(self):
        for p in (ROOT/'build-tools/FindDotNet10.cmd',ROOT/'CHECK_INSTALLER_BUILD_ENV.cmd',
                  ROOT/'BUILD_RELEASE_ONEDIR.cmd'):
            b=p.read_bytes()
            self.assertTrue(b'\r\n' in b, str(p))
            self.assertNotIn(b'\n',b.replace(b'\r\n',b''),str(p))
            self.assertTrue(b.isascii(),str(p))

if __name__=='__main__': unittest.main()
