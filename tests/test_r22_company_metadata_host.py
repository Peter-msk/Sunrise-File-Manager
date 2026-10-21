"""R22 build fix contracts, based on R21 Windows logs.

These tests verify source wiring and Windows command syntax; the actual host policy
and .NET execution must still be validated on the Windows build PC.
"""
from pathlib import Path
import unittest
import re

ROOT=Path(__file__).resolve().parents[1]

class MetadataHostAndReducedPreflight(unittest.TestCase):
    def setUp(self):
        self.entry=(ROOT/'BUILD_ON_WINDOWS_COMPANY.cmd').read_text('ascii')
        self.installer=(ROOT/'BUILD_RELEASE_INSTALLER.cmd').read_text('ascii')
        self.payload=(ROOT/'BUILD_RELEASE_ONEDIR.cmd').read_text('ascii')

    def test_no_duplicate_toolchain_checklist(self):
        self.assertNotIn('call "%ROOT%\\CHECK_INSTALLER_BUILD_ENV.cmd"',self.entry)
        self.assertIn('BUILD_RELEASE_INSTALLER.cmd" --company --no-pause',self.entry)
        self.assertNotIn('call :fail_env',self.entry)
        self.assertNotIn('--list-sdks',self.payload)
        self.assertIn('FindDotNet10.cmd',self.payload)
        self.assertNotIn('findstr /X /C:"STATUS=SUCCESS"',self.installer)

    def test_uses_dotnet_host_for_dll_not_unsigned_exe(self):
        s=self.installer
        self.assertIn('build "%METADATA_PROJECT%" --configuration Release -p:UseAppHost=false',s)
        self.assertIn('"%SUNRISE_DOTNET_RESOLVED%" "%METADATA_DLL%" "%ROOT%" "%FINAL_SETUP%" "%SFM_APP_VERSION%"',s)
        self.assertNotIn(' run --project ',s)
        self.assertNotIn('ReleaseMetadata.exe',s.split('"%SUNRISE_DOTNET_RESOLVED%" "%METADATA_DLL%"')[0].replace('REM ReleaseMetadata.exe',''))
        self.assertIn('R16_metadata_build.log',s)
        self.assertIn('R16_release_metadata.log',s)

    def test_security_integrity_runtime_gates_kept(self):
        s=self.installer
        for phrase in ('EnsureSunriseGroupSigning.cmd', '/SSunriseGroup=', '"%SIGNTOOL%" verify /pa /v',
                       'R15_SIGNED_SETUP_SHA256.txt','RELEASE_ARTIFACT_VERIFY','goto :fail_metadata',
                       'if not exist "%CERT_FILE%" goto :fail_payload',
                       'if not exist "%ROOT%\\COMPANY_SOURCE_ONLY.flag" goto :fail_company'):
            with self.subTest(gate=phrase):self.assertIn(phrase,s)
        self.assertLess(s.index('STAGE=INSTALLER_VERIFY'),s.index('STAGE=RELEASE_METADATA'))
        self.assertLess(s.index('STAGE=RELEASE_METADATA'),s.index('STAGE=RELEASE_ARTIFACT_VERIFY'))
        self.assertIn('STATUS=PINNED_CERT_MISSING_OR_INVALID',(ROOT/'build-tools/EnsureSunriseGroupSigning.cmd').read_text('ascii'))

    def test_builder_cmd_ascii_crlf_and_stable_version(self):
        for name in ('BUILD_ON_WINDOWS_COMPANY.cmd','BUILD_RELEASE_ONEDIR.cmd','BUILD_RELEASE_INSTALLER.cmd'):
            b=(ROOT/name).read_bytes()
            self.assertTrue(b.isascii(),name)
            self.assertIn(b'\r\n',b)
            self.assertNotIn(b'\n',b.replace(b'\r\n',b''),name)
        self.assertEqual((ROOT/'installer/RELEASE_VERSION.txt').read_text().strip(),'1.2.0')
        self.assertIn('1.0.0',(ROOT/'latest.json').read_text('utf8'))

if __name__=='__main__': unittest.main()
