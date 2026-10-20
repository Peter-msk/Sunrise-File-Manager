"""R19 R08 development/official release isolation contracts (no Windows operations)."""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEV = (ROOT / 'BUILD_DEV_EXE.cmd').read_text(encoding='ascii')
OFFICIAL = (ROOT / 'BUILD_ON_WINDOWS_COMPANY.cmd').read_text(encoding='ascii')
INSTALLER = (ROOT / 'BUILD_RELEASE_INSTALLER.cmd').read_text(encoding='ascii')
PAYLOAD = (ROOT / 'BUILD_RELEASE_ONEDIR.cmd').read_text(encoding='ascii')
DOC = (ROOT / 'docs/R19_DEV_BUILD_WITHOUT_SIGNTOOL_R08.md').read_text(encoding='utf-8')


class UnsignedDevelopmentModeR08(unittest.TestCase):
    def test_dev_builder_is_dotnet_only(self):
        self.assertIn('call "%ROOT%\\build-tools\\FindDotNet10.cmd"', DEV)
        self.assertEqual(len(re.findall(r'"%DOTNET_EXE%" publish ', DEV)), 1)
        self.assertIn('-p:SunriseCompanyBuild=true', DEV)
        self.assertIn('--self-contained true', DEV)
        self.assertIn('-p:PublishSingleFile=false', DEV)
        self.assertIn('net10.0-windows10.0.19041.0', DEV)
        for disallowed in ('FindSignTool.cmd', 'FindInnoSetup.cmd', 'EnsureSunriseGroupSigning.cmd',
                           'CHECK_INSTALLER_BUILD_ENV.cmd', 'SignTool.exe', 'signtool.exe',
                           'certreq.exe', 'certutil.exe', 'ISCC.exe', 'LogicTests.csproj',
                           'UiSmokeTests', 'powershell.exe', 'Set-MpPreference'):
            self.assertNotIn(disallowed, DEV)

    def test_dev_output_is_separate_from_signed_installer(self):
        self.assertIn('SunriseFileManager_DEV_UNSIGNED', DEV)
        self.assertIn('_SFM_DEV_UNSIGNED_staging', DEV)
        self.assertIn('DEVELOPMENT_ONLY_UNSIGNED.txt', DEV)
        self.assertIn('SIGNED=NO', DEV)
        self.assertIn('OFFICIAL_RELEASE=NO', DEV)
        self.assertNotIn('Sunrise_File_Manager_1.0.0_Setup.exe', DEV)
        self.assertIn('if errorlevel 1 goto :fail_publish', DEV)
        self.assertLess(DEV.index('"%DOTNET_EXE%" publish'), DEV.index('move "%FINAL%" "%PREVIOUS%"'))
        self.assertIn('if exist "%PREVIOUS%" move "%PREVIOUS%" "%FINAL%"', DEV)

    def test_check_only_does_not_publish(self):
        self.assertIn('--check-only', DEV)
        self.assertIn('if "%CHECK_ONLY%"=="1" goto :check_pass', DEV)
        self.assertLess(DEV.index('if "%CHECK_ONLY%"=="1" goto :check_pass'), DEV.index('"%DOTNET_EXE%" publish'))
        self.assertIn('STATUS=READY_FOR_DEV_BUILD', DEV)
        self.assertIn('SIGNTOOL=NOT_REQUIRED', DEV)
        self.assertIn('INSTALLER=NOT_REQUIRED', DEV)
        self.assertIn('CERTIFICATE=NOT_REQUIRED', DEV)

    def test_official_signing_remains_fail_closed(self):
        self.assertIn('BUILD_RELEASE_INSTALLER.cmd', OFFICIAL)
        self.assertIn('FindSignTool.cmd', INSTALLER)
        self.assertIn('goto :fail_signtool', INSTALLER)
        self.assertIn('FindSignTool.cmd', PAYLOAD)
        self.assertIn('EnsureSunriseGroupSigning.cmd', PAYLOAD)
        self.assertIn('"%SIGNTOOL%" sign /fd SHA256', PAYLOAD)
        self.assertIn('"%SIGNTOOL%" verify /pa /v', INSTALLER)
        self.assertNotIn('BUILD_DEV_EXE.cmd', INSTALLER + PAYLOAD + OFFICIAL)

    def test_company_policy_crlf_and_handover(self):
        b = (ROOT / 'BUILD_DEV_EXE.cmd').read_bytes()
        self.assertTrue(b.isascii())
        self.assertIn(b'\r\n', b)
        self.assertNotIn(b'\n', b.replace(b'\r\n', b''))
        self.assertIn('SOURCE / STATIC CONTRACT PASS', DOC)
        self.assertIn('Windows', DOC)
        self.assertIn('미서명', DOC)

if __name__ == '__main__':
    unittest.main()
